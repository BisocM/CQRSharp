# RabbitMQ transport

`CQRSharp.RabbitMQ` carries **integration events** between services through RabbitMQ. A notification you tell it to
publish leaves the process **through the outbox**: it is stored with the request's data, sent at least once, in order
per partition key, and counts as sent only once the broker has confirmed it. A queue you tell it to consume brings
notifications **into the outbox**, deduplicated by message id and acknowledged only once stored, and the outbox
processor then delivers them to the local handlers with their own retries, dead letters and ordering. The package is
Native-AOT compatible.

- [Install](#install)
- [How it works](#how-it-works)
- [Publishing](#publishing)
- [Consuming](#consuming)
- [Topology](#topology)
- [Connections](#connections)
- [Configuration reference](#configuration-reference)
- [Delivery guarantees](#delivery-guarantees)
- [Ordering](#ordering)
- [Dead letters: two places](#dead-letters-two-places)
- [The wire format](#the-wire-format)
- [Health check](#health-check)
- [Observability](#observability)
- [Failure modes](#failure-modes)
- [Configuration checks](#configuration-checks)
- [Native AOT](#native-aot)
- [Other transports](#other-transports)

## Install

```shell
dotnet add package CQRSharp.RabbitMQ
```

The transport lives in the outbox, so the outbox must be on: add it inside `UseOutbox(...)`, next to the store.

```csharp
services.AddCqrsGenerated(b => b
    .UseEntityFrameworkCoreUnitOfWork<AppDbContext>()
    .UseOutbox(o => o
        .UseEntityFrameworkCore<AppDbContext>()
        .UseRabbitMq("amqp://user:password@rabbit:5672/", r => r
            .Publish<OrderPlaced>()                                     // leaves the process
            .Consume("billing", q => q.Bind<PaymentCaptured>()))));     // comes in
```

It builds on RabbitMQ.Client 7.2 and is tested against RabbitMQ 4.1. Quorum queues, the default, need RabbitMQ 3.8 or
later.

## How it works

The broker is **one more subscriber of the outbox**, called a *transport*. A published notification that a transport
forwards is stored with one extra outbox message, addressed to the transport by its name (`rabbitmq` unless you
[name it](#configuration-reference)), next to the messages of the local handlers:

```
Publish(OrderPlaced) inside a transactional request
   └─ one outbox write (with the request's data, when the store joins the unit of work)
        ├─ message → Shop.Billing.ChargeCard        a local handler, as before
        └─ message → rabbitmq                        the transport: published to the broker
   the messages share the notification id, the creation (or due) time, the partition key and the trace parent

outbox processor claims the rabbitmq message
   └─ confirmed, mandatory, persistent publish of the stored payload, byte for byte
        confirmed → processed | unreachable → deferred, no attempt | refused → an attempt, then a dead letter
```

So every outbox guarantee covers the publish: it is atomic with the data (with a store that joins the unit of work),
at least once, leased, retried with back-off, dead-lettered, ordered per partition key, and measured by the outbox
gauges. A notification is still delivered to its **local handlers**; one that has none is stored for the transport
alone. The transport's message has its own attempts and dead letter, so a broker that fails never makes a local handler
run again, and a local handler that fails never publishes again. No pipeline behavior runs around a publish: there is no
handler, and the payload is never deserialized.

On the receiving side, a consumer hands each message to the **intake**, which stores one outbox message per local
handler. The processor then delivers them exactly as it delivers what the application publishes itself. What comes in
through a transport is **never published back out**, so a service that both consumes and publishes a notification does
not loop.

## Publishing

`Publish<T>()` forwards every `T`; `Publish("name")` every notification stored under that name (a notification declared
in another service's contracts assembly, or named by a custom serializer). A forwarded notification must be durable: its
stable name (`[NotificationName]`) is the routing key and the name the receiver reads it back by. Publishing the runtime
type as a base type or `INotification` routes it the same way. Configure the same notification twice, and the last
configuration wins.

```csharp
.UseRabbitMq(uri, r => r
    .Publish<OrderPlaced>()                                  // exchange cqrsharp.notifications, key "orders.placed"
    .Publish<OrderShipped>(p => p.WithRoutingKey("orders.shipped.v1"))
    .Publish<StockAdjusted>(p => p.ToExchange("inventory"))  // another durable topic exchange, declared too
    .Publish<Heartbeat>(p => p.AllowUnroutable())            // may go unheard
    .Publish("tenant.ledger.posted"))
```

A send is a **mandatory, persistent** publish on a channel with **publisher confirms**, one send per channel at a time,
bounded by `PublishTimeout`. It counts as sent only when the broker **confirms** it, which it does once every queue the
message is routed to has it (replicated to a majority of a quorum queue). What each outcome does to the outbox message:

| What happened | Send result | The outbox message |
| --- | --- | --- |
| The broker confirmed the publish | Sent | Processed. |
| No connection, the connection is blocked (a resource alarm), the broker nacked, or no confirm within `PublishTimeout` | Unavailable | Deferred until the connection may be back, **without an attempt**: an outage never dead-letters anything. After a timeout the broker may have the message, so the retry may deliver it twice, with the same message id. |
| No queue is bound to receive it (the broker returned the mandatory publish) | Rejected, unless the publication `AllowUnroutable()`, which counts it as sent and drops it | An attempt, with back-off; a dead letter once the attempts run out. A consumer that binds later gets the retries. |
| The exchange does not exist (404), or the broker refused (403, 406) | Rejected | An attempt; a dead letter at the last. |
| The payload is larger than `MaxMessageSize`, or the broker refused it as too large; or the transport no longer publishes that name | Rejected permanently | A dead letter at once. |
| The host is stopping | (cancelled) | Handed back with the batch, and sent after the restart. |
| Anything else threw | Rejected | An attempt. |

A dead letter is addressed to the transport (`HandlerName = "rabbitmq"`): `GetDeadLettersAsync` lists it with its last
error, and `RequeueAsync` sends it again once the cause is fixed.

A notification [scheduled for later](outbox.md#scheduled-publishing) is published when it falls due: the transport's
message carries the same due time as the handlers' messages, and no broker plugin is involved. The delay's accuracy is
the outbox's (the polling interval plus the claim), not the broker's.

Under the `Transactional` outbox mode, a notification a transport forwards must be published inside a transaction:
outside one it would be delivered in-process, where no transport can take it, so the publish fails (`CQRCONF016`).

Throughput is one round trip per send and per concurrent delivery slot
(`OutboxProcessorOptions.MaxDegreeOfParallelism`): the next message of a partition is published only once the previous
one is confirmed, which is what keeps the order.

## Consuming

`Consume("queue", q => ...)` consumes a queue. The queue's name is the service's subscription: every instance of the
service consumes the same queue, and each message goes to one of them.

```csharp
.UseRabbitMq(uri, r => r
    .Consume("billing", q => q
        .Bind<OrderPlaced>()                         // the name OrderPlaced is stored under, on the default exchange
        .Bind("payments.#", exchange: "payments")    // a topic pattern, on another exchange
        .Prefetch(64)
        .Lanes(8)
        .SingleActiveConsumer()))
```

Each message is copied off the channel, queued on its **lane**, handed to the intake in a DI scope of its own, and
**acknowledged only once the intake has stored it**. The intake deduplicates by the message's `message-id` through the
inbox (under the source `<transport>:<queue>`), reads the notification back by its `type` property, and stores one
outbox message per local handler; with the EF Core store, inbox and unit of work over one `DbContext`, the stored
messages and the dedupe record are one commit. What the broker then sees:

| The intake's outcome | Broker | Log / `cqrsharp.transport.received` outcome |
| --- | --- | --- |
| Stored | ack | 5100 (Debug); `stored` |
| Taken in before (the inbox knows its message id) | ack | 5101 (Debug); `duplicate` |
| No local handler receives it | ack, nothing stored | 5102 (Debug); `no_subscribers` |
| The payload cannot be read | reject → dead-letter queue | 8030 (Error); `unreadable` |
| No `type` property | reject → dead-letter queue | 8034 (Error); `unreadable` |
| The name is not known here, and the message is younger than the unknown-recipient grace period | held, tried again | 8031 (Information); `unknown_held` |
| The name is not known here, past the grace period | reject → dead-letter queue | 8032 (Error); `unknown` |
| The intake failed (the store or the inbox is unreachable) | held, tried again with the outbox's retry back-off | 8033 (Warning); `failed` |
| Held for `MaxHold` without being taken in | nack, back to the queue | 8035 (Warning) |

**Holding rather than requeueing.** A message that cannot be taken in yet stays unacknowledged on its lane and is tried
again after a delay: a requeue would come straight back (a hot loop) and may lose its place. It is never held longer
than `MaxHold` (5 minutes, validated to stay below the broker's `consumer_timeout`, 30 minutes by default), after which
it is returned to the queue; the queue's delivery limit then bounds how often that happens. A notification this service
does not know is held on the same terms as the outbox processor defers one: a newer instance of a fleet running mixed
versions may know it, until `OutboxProcessorOptions.UnknownRecipientGracePeriod` (1 hour) has passed since it was
published (its `cqrsharp-created-at` header, else its `timestamp`, else its arrival).

**Lanes.** `Lanes` (4) messages are taken in at once. Messages that share a partition key (the `cqrsharp-partition-key`
header) always go through the same lane, one after another in the order they arrived; messages without a key go to the
least busy lane. A held message holds back only the rest of its lane. `Prefetch` (32) is how many unacknowledged
messages the broker hands the consumer.

**Single active consumer.** RabbitMQ delivers a queue in order to one consumer; with several consumers on one queue the
order across them is lost. `SingleActiveConsumer()` declares the queue with `x-single-active-consumer`: one instance
consumes and the others stand by, taking over when it goes. Use it when the order per key matters across instances.

**Shutdown.** When the host stops, the consumer cancels its subscription, lets the intakes that are writing finish (they
are cancelled only if the host's `ShutdownTimeout` runs out), and releases the messages it holds or has not started
(8023): unacknowledged, they return to the queue when the channel closes. The connections are closed with the service
provider, after the outbox processor has stopped, so the sends in flight finish or are handed back first.

## Topology

The transport declares what it uses, **idempotently**, on every connection it opens: declaring an exchange or queue that
exists with the same settings changes nothing. Each side declares its own: the publisher its exchanges; a consumer the
exchanges of its bindings, its queue, its dead-letter exchange and queue, and its bindings.

| What | Default |
| --- | --- |
| Exchange | `cqrsharp.notifications` (`DefaultExchange`), a durable **topic** exchange. Dotted names fit topic patterns: bind `orders.*` or `orders.#`. |
| Routing key | The notification's name, unless `WithRoutingKey(...)`. |
| Queue | Named by `Consume("queue", ...)`, durable, a **quorum** queue (`Classic()` for a development broker), with `x-delivery-limit` 20 (`DeliveryLimit(...)`; quorum queues only). |
| Dead letters | A durable **direct** exchange `cqrsharp.dead-letter` (`DeadLetterExchange`), and a queue `<queue>.dead-letter` bound to it by the queue's name; the queue dead-letters there (`x-dead-letter-exchange`, `x-dead-letter-routing-key`). `DeadLetterQueue(false)` leaves them out, and the broker drops what it would dead-letter. |
| Bindings | Only those you declare: `Bind<T>()`, `Bind("pattern")`, each on the default exchange unless you name another. |

**`AssumeExistingTopology()`** declares nothing, for a topology a platform team owns. A missing exchange then shows up
as rejected publishes, and a missing queue as a consumer that cannot start.

A declaration the broker refuses, typically `PRECONDITION_FAILED` because a queue exists with other arguments (a classic
queue declared as quorum), is logged (8011), reported unhealthy by the [health check](#health-check), and tried again at
the longest back-off, so the consumer starts without a restart once an operator has fixed it. Publishing to other
exchanges is unaffected.

## Connections

| Verb | Connection |
| --- | --- |
| `UseRabbitMq("amqp://...")` | The transport opens its own: one to publish on and one to consume on (the broker throttles a publishing connection under flow control, which would stall a consumer's acknowledgements on the same connection). It reopens a lost connection itself, declares its topology and consumes again on the new one, and closes both with the service provider. |
| `UseRabbitMq(sp => sp.GetRequiredService<IConnectionFactory>())`, an `IConnectionFactory` factory | The same, from the application's factory: its address, credentials and TLS settings. |
| `UseRabbitMq(connection)`, an `IConnection` | The application's connection, for publishing and consuming alike, never closed by CQRSharp. It should recover by itself (automatic recovery, the client's default); the transport waits while it is down. |
| `UseRabbitMq(sp => sp.GetRequiredService<IConnection>())`, a factory | The same, from the container, such as Aspire's `AddRabbitMQClient`; it runs once per service provider. |

**The broker being down never fails the host.** The connections are opened in the background, retried with a back-off
that starts at one second and doubles up to `ReconnectMaxDelay` (30 seconds). Meanwhile sends are Unavailable and their
messages wait in the outbox, consumers wait for their connection, and the health check reports the connection closed. A
connection the broker **blocks** (a memory or disk alarm) makes sends Unavailable at once rather than waiting out their
confirm, until it is unblocked (8004, 8005).

`AddCqrsRabbitMq(...)` on `IServiceCollection` takes the same four forms, for an application that wires its outbox with
`services` calls; the outbox must still be turned on (`CQRCONF013`).

## Configuration reference

`RabbitMqTransportBuilder` (namespace `CQRSharp.RabbitMQ`), the callback of `UseRabbitMq`:

| Verb | Effect |
| --- | --- |
| `Name(string)` | The transport's name, `rabbitmq` by default: what its outbox messages are addressed to and what its options are named after. Give a second transport (a second broker) a name of its own. It must not change while messages addressed to it may be stored, and must not be the name of a notification handler (`CQRCONF015`). At most 256 characters. |
| `Publish<T>(p => ...)` / `Publish("name", p => ...)` | Forwards a notification; `p` is a `RabbitMqPublication`: `ToExchange(...)`, `WithRoutingKey(...)`, `AllowUnroutable()`. |
| `Consume("queue", q => ...)` | Consumes a queue; `q` is a `RabbitMqConsumerBuilder` (below). |
| `AssumeExistingTopology()` | Declares nothing. |
| `Configure(o => ...)` | Adjusts the `RabbitMqTransportOptions` (below). |

`RabbitMqConsumerBuilder`:

| Verb | Default | Effect |
| --- | --- | --- |
| `Bind<T>(exchange?)` / `Bind("pattern", exchange?)` | none | Binds the queue to a notification's name, or to a routing pattern. |
| `Prefetch(ushort)` | 32 | Unacknowledged messages the broker hands the consumer; 1 to 65535. |
| `Lanes(int)` | 4 | Messages taken in at once, ordered per partition key; 1 to 64. |
| `SingleActiveConsumer(bool)` | off | `x-single-active-consumer`. |
| `Classic()` | quorum | A classic queue, which has no delivery limit. |
| `DeliveryLimit(int?)` | 20 | `x-delivery-limit`, at least 1; `null` leaves the broker's default. Quorum queues only. |
| `DeadLetterQueue(bool)` | on | The queue's dead-letter queue. |
| `MaxHold(TimeSpan)` | 5 min | The longest a message is held unacknowledged; above zero and below 30 minutes. |

`RabbitMqTransportOptions`, named options per transport (validated at host start in every environment):

| Option | Default | Meaning |
| --- | --- | --- |
| `DefaultExchange` | `cqrsharp.notifications` | The topic exchange publications and bindings use unless they name another. |
| `DeadLetterExchange` | `cqrsharp.dead-letter` | The direct exchange consumer queues dead-letter to. |
| `DeclareTopology` | `true` | `false` is `AssumeExistingTopology()`. |
| `PublishTimeout` | 30 s | How long a publish waits for its confirm before it is Unavailable. |
| `MaxMessageSize` | 16 MiB | The largest payload published; keep it at or below the broker's `max_message_size`. At most 512 MiB. |
| `AppId` | the host's application name | The `app-id` of published messages. |
| `ContentType` | `application/json` | The `content-type` of published messages; set it when a custom notification serializer writes another format. |
| `ClientProvidedName` | `<app-id>/<transport name>` | The prefix of the connection names the management UI shows (then `/publish`, `/consume`). |
| `ReconnectMaxDelay` | 30 s | The longest wait between two connection attempts, or before consuming again; at least 1 second. |

Exchange, queue and routing-key names are at most 255 bytes, as AMQP allows, and a transport's name and a queue's name
together at most 255 characters, the inbox's source name.

## Delivery guarantees

**Publishing.** A forwarded notification published inside a request is stored with the request's data: atomically when
the store joins the unit of work, otherwise right after the commit (the outbox's usual window). It is then published
**at least once**, only while the broker is reachable, and counts as sent only once the broker confirms a mandatory,
persistent publish. It is never dead-lettered because the broker was unreachable. A duplicate is possible when a crash
or a lost lease falls between the confirm and the processed mark, or when a confirm timed out although the broker had
the message; every duplicate carries the same `message-id`.

**Consuming.** A message is acknowledged only once its intake is durable. A redelivery with the same `message-id` is
recognised by the intake's inbox record: **exactly once** with the EF Core store, inbox and unit of work over one
`DbContext` (the messages and the record are one commit); otherwise **at least once**, with a duplicate confined to a
crash between storing and recording, or to a redelivery after the inbox retention (7 days by default). A message without
a `message-id` cannot be deduplicated. Each local handler then has the outbox's usual guarantees.

**End to end.** For a handler whose inbox joins its transaction on both sides: exactly-once effects for what the handler
writes through that context, over an at-least-once broker. Otherwise the handler must be idempotent, as with any outbox
delivery.

**Loss.** None, except: the outbox's window after a commit when the store does not join the unit of work; a message
dead-lettered (in the outbox or in a dead-letter queue), which is kept, not lost; an `AllowUnroutable()` publication no
queue is bound to receive, dropped by design; and what the broker itself loses (a classic, non-replicated queue that
fails, a queue length limit or TTL of your own).

## Ordering

What CQRSharp can promise, and does:

1. **Into the broker.** The outbox never hands out the next message of a partition key while the previous one is pending
   or in progress, and marks it processed only after its confirm. So message *N* is in every queue it is routed to
   before *N+1* is published, whichever channel or instance publishes *N+1*: across a whole fleet of producers.
2. **Out of a queue.** RabbitMQ delivers a queue in order to one consumer. With several instances, use
   `SingleActiveConsumer()`.
3. **Into the handlers.** The lanes keep each key in order inside the consumer, the intake stores in that order, and the
   outbox delivers each (key, handler) in order.

*Notifications that share a partition key, published by one service through the outbox to one exchange, reach each
local handler of a consuming service in the order they were stored, provided the consumer's queue has a single active
consumer.* The edges: a producer message dead-lettered after its last attempt releases its partition, as a local dead
letter does; after a consumer's channel is lost, the broker requeues its unacknowledged messages but does not guarantee
their original place, so a key's in-flight messages may be redelivered out of order (at most one per lane when
`Prefetch` is not above `Lanes`); what is rejected to a dead-letter queue leaves the order; and two queues bound to the
same exchange are ordered independently.

## Dead letters: two places

- **Outbox dead letters** (`IOutboxStore.GetDeadLettersAsync`): a publish that was refused until its attempts ran out
  (`HandlerName` = the transport), and a *local handler* that failed until its attempts ran out, a received notification
  included. Handler failures never touch the broker.
- **Broker dead-letter queues** (`<queue>.dead-letter`): what the *intake* could not take in (an unreadable payload, a
  message without a `type`, a name unknown past the grace period) and what exceeded the queue's delivery limit. Inspect
  and replay them with the broker's tools.

## The wire format

What a service outside CQRSharp exchanges with it:

| AMQP property or header | Value |
| --- | --- |
| `message-id` | The outbox message's id, as 32 hex digits (`N` format): the same on every send of it, the receiver's dedupe key. A receiver accepts any string: one that is not a GUID is mapped to a name-based UUID. |
| `type` | The notification's stable name: what the receiver reads it back by. Required. |
| body | The payload the notification serializer wrote: JSON under the source-generated serializer. |
| `content-type` | `ContentType` (`application/json`). |
| `delivery-mode` | 2, persistent. |
| `timestamp` | When the notification was published (or fell due), whole seconds. |
| `app-id` | `AppId`. |
| `cqrsharp-created-at` | The same time, ISO 8601 round-trip (`O`). |
| `cqrsharp-notification-id` | The id every message of one publish shares, local deliveries included. |
| `cqrsharp-partition-key` | The partition key, when there is one: what the consumer's lanes order by. |
| `traceparent`, `tracestate` | The W3C trace context of the publish. |

Headers are written as strings; a receiver reads them as the bytes RabbitMQ.Client hands back, or as strings. A producer
outside CQRSharp needs to send `type`, a `message-id` for deduplication, and the payload the receiving serializer reads.

## Health check

```csharp
services.AddHealthChecks().AddCqrsRabbitMq();   // name "cqrsharp.rabbitmq"; failureStatus and tags optional
```

It reports every RabbitMQ transport of the application: the failure status while a connection is not open, while the
publisher's exchanges cannot be declared, or while a consumer's topology or subscription is refused; **Degraded** while
the broker blocks a connection or a consumer is between channels; **Healthy** otherwise. The data holds each
connection's state (`rabbitmq.publish`: `open`, `blocked`, `closed`) and each queue's (`rabbitmq.queue.billing`:
`Consuming`, ...). During an outage the [outbox health check](diagnostics.md#health-checks) shows the other half: the
lag of the messages waiting for the broker.

## Observability

A publish runs inside the processor's `CQRS Outbox Dispatch` span, which gets the attributes `messaging.system`
(`rabbitmq`), `messaging.destination.name` (the exchange), `messaging.rabbitmq.destination.routing_key` and
`messaging.message.id`, and is counted by `cqrsharp.outbox.messages` with the transport's name as the handler (an outage
is the outcome `unavailable`). A message taken in gets a `CQRS Transport Receive` span, parented to the publisher's
trace, and is counted by `cqrsharp.transport.received`; the handlers' deliveries continue that trace. CQRSharp writes
the `traceparent` header itself, so the trace crosses the broker with or without RabbitMQ.Client's own instrumentation;
to add the client's publish and deliver spans too, add its sources:

```csharp
.WithTracing(t => t.AddSource(CqrsTelemetry.ActivitySourceNames).AddSource("RabbitMQ.Client.*"))
```

The log lines are listed in [Observability](observability.md#event-ids) (8000 block, and 5029 to 5031, 5100 to 5104).

## Failure modes

| Failure | What happens | Signal |
| --- | --- | --- |
| Broker down when the host starts | The host starts; sends are deferred, consumers wait and retry the connection. | 8003, health check, outbox lag |
| Connection lost mid-send | Unavailable, deferred; the broker may end up with the message twice. | 8001, `unavailable` |
| No confirm within `PublishTimeout` | Unavailable, sent again later (possibly twice). | `unavailable` |
| Broker nack (a queue that rejects publishes when full, a quorum queue without its majority) | Unavailable. | 8041 (Debug), `unavailable` |
| Nothing bound to the routing key | Rejected: attempts, then an outbox dead letter; dropped with `AllowUnroutable()`. | 5031, 5015, 8040 (Debug) |
| Exchange deleted | Rejected; declared again on the next connection. | 5031 |
| Message too large | Dead-lettered at once. | 5030 |
| Resource alarm | Sends deferred until unblocked. | 8004, 8005 |
| Producer crash after the confirm, before the mark | Sent again: a duplicate with the same message id, which the consumer recognises. | none needed |
| An instance without the transport claims its message (rolling deploy) | Deferred for an instance that has it, dead-lettered after the grace period. | 5025, 5008 |
| Consumer crash before the acknowledgement | Redelivered; recognised if its intake had finished. | none needed |
| Store or inbox down during an intake | Held with back-off up to `MaxHold`, then returned to the queue; the delivery limit ends the loop. | 8033, 8035 |
| Name unknown (consumer older than the producer) | Held until the grace period ends, then dead-lettered. | 8031, 8032 |
| Unreadable payload or no `type` | Dead-lettered at once. | 8030, 8034 |
| No local handler | Acknowledged and dropped. | `no_subscribers` |
| Local handler fails | Outbox attempts and dead letter; the broker is not involved. | 5014, 5015 |
| Queue deleted under the consumer | Declared (unless assumed existing) and consumed again. | 8022 |
| Topology refused | That consumer does not start; retried at the longest back-off; other traffic continues. | 8011, health check |

## Configuration checks

The transport is checked with the other [`CQRCONF`](diagnostics.md#startup-validation-cqrconf) rules, at startup and at
first use: `CQRCONF013` a transport while the outbox is off (host start fails in every environment), `CQRCONF014` a
forwarded or bound type the serializer does not name, or a forwarded name no module has (a publish of such a type
fails), `CQRCONF015` a transport name that clashes with a handler's or another transport's (nothing is stored and the
processor does not start), `CQRCONF016` a forwarded notification published outside a transaction under `Transactional`
(the publish fails), and `CQRCONF017` a bound notification no local handler receives (a warning).

## Native AOT

The package is AOT-compatible: its own code is checked by the trim and AOT analyzers, and a Native AOT publish of an
application that publishes and consumes through it reports no trim or AOT warning on net8.0 and net10.0, RabbitMQ.Client
included. The repository's AOT canary runs that application against a real broker. See [Native AOT](native-aot.md).

## Other transports

The transport is built on a small extension point in `CQRSharp.Abstractions`: `INotificationTransport` (what the outbox
forwards through), `INotificationIntake` (what a consumer takes received notifications in through), and
`OutboxStoreBuilder.AddTransport(...)`. It ships as experimental in 5.x (`CQREXP001`, see
[Diagnostics](diagnostics.md#experimental-apis-cqrexp)): a later minor release may reshape it after real use. A
transport proves itself against `NotificationTransportContractTests` ([The testing
packages](testing-package.md#contract-testing-a-notification-transport)).
