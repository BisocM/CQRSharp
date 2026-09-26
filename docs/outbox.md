# The outbox

The **outbox** gives notifications **at-least-once delivery** that survives crashes. Instead of running the handlers of
a published notification in-process, where a crash after the database commit loses the event, the notification is
stored, and a background processor delivers it afterwards: **to each handler separately**, retried with back-off, and
**in order** for the notifications that ask for it. With a store that joins the unit of work's transaction, the stored
notification also commits **atomically** with the data it announces.

- [Why an outbox](#why-an-outbox)
- [Enabling the outbox](#enabling-the-outbox)
- [Outbox modes](#outbox-modes)
- [Which notifications are durable](#which-notifications-are-durable)
- [How a publish reaches the store](#how-a-publish-reaches-the-store)
- [One message per handler](#one-message-per-handler)
- [Ordered delivery](#ordered-delivery)
- [The processor](#the-processor)
- [The inbox: effectively-once delivery](#the-inbox-effectively-once-delivery)
- [Dead letters](#dead-letters)
- [Backlog, gauges and the health check](#backlog-gauges-and-the-health-check)
- [Stores](#stores)
- [Custom stores](#custom-stores)
- [At-least-once and idempotent handlers](#at-least-once-and-idempotent-handlers)

## Why an outbox

The classic *dual-write* problem: you commit a database change **and** publish an event. If the process dies between
the two, the system is inconsistent: the data changed but nobody was told, or the event went out but the data did not
commit. The outbox makes the event part of the write: it is stored with the data (or right after the data commits),
and delivered out of band with retries.

## Enabling the outbox

The outbox is off by default. `UseOutbox` turns it on, selects the mode and registers a store in one step:

```csharp
services.AddCqrsGenerated(b => b
    .UseEntityFrameworkCoreUnitOfWork<AppDbContext>()
    .UseOutbox(o => o
        .UseEntityFrameworkCore<AppDbContext>()      // durable store; Enabled() is the default mode
        .ConfigureProcessor(p =>
        {
            p.PollingInterval = TimeSpan.FromSeconds(2);
            p.BatchSize       = 200;
            p.MaxAttempts     = 5;
        })));
```

`OutboxStoreBuilder` (namespace `CQRSharp.Pipelines`) verbs:

| Verb | Effect |
| --- | --- |
| `Enabled()` *(default)* | Mode `Enabled`. |
| `Transactional()` | Mode `Transactional`. Needs a unit of work. |
| `UseInMemoryStore(configure?)` | The non-durable in-memory store: development, tests and single-node demos only; messages are lost on restart. `InMemoryOutboxStoreOptions`: `VisibilityTimeout` (5 min), `InboxRetention` (7 days), `DeadLetterRetention` (`null`, kept); each greater than zero and at most 10 years. |
| `UseRedis(...)` | The Redis store (`CQRSharp.Redis`); see [Integrations](integrations.md#redis). |
| `UseEntityFrameworkCore<TContext>()` | The EF Core store (`CQRSharp.EntityFrameworkCore`); see [Integrations](integrations.md#entity-framework-core). |
| `UseStore(Action<IServiceCollection>)` | Registers any `IOutboxStore` (and its `IInboxStore`). The hook the integration packages build on. |
| `ConfigureProcessor(Action<OutboxProcessorOptions>)` | Tunes the [processor](#the-processor). Repeated calls, across every `UseOutbox`, all run in call order. |

A chosen store replaces every outbox and inbox store already registered, whatever the order relative to
`AddCqrsGenerated`; the last explicit choice wins. A `UseOutbox(...)` that chooses no store uses the in-memory store
only when no outbox store is registered at all, so a bare `.UseOutbox(o => { })` is a working development configuration
and never shadows a durable store registered elsewhere.

The processor is a hosted service that every `AddCqrsGenerated` registration adds. It stays idle while the outbox is
off, so there is nothing else to register. Outside the builder, `services.Configure<OutboxProcessorOptions>(...)` tunes
it and `OutboxOptions.Mode` selects the mode.

## Outbox modes

`OutboxMode` (on `OutboxOptions`, namespace `CQRSharp`):

| Mode | Behavior |
| --- | --- |
| `Disabled` *(default without `UseOutbox`)* | The outbox is off; `Publish` always runs the handlers in-process. |
| `Enabled` *(default of `UseOutbox`)* | Every [durable](#which-notifications-are-durable) notification goes through the outbox. |
| `Transactional` | A durable notification goes through the outbox only while the scope's `IUnitOfWork` has an active transaction; published outside one, it runs in-process at once. |

`Enabled` together with a [unit of work](unit-of-work.md) is already atomic for a store that joins the transaction, so
choose `Transactional` only when notifications published outside transactional work should stay in-process.
`Transactional` without any registered `IUnitOfWork` never sees a transaction, so nothing could reach the outbox: a
publish of a notification the serializer names fails with **CQRCONF007**, as does the startup validator (see
[Diagnostics](diagnostics.md#first-use-checks)).

## Which notifications are durable

The application has exactly one `INotificationSerializer` (namespace `CQRSharp.Persistence`), and it alone decides what
is durable: while an outbox mode is active, a notification the serializer names is stored under that name, and any
other runs in-process. By default the serializer is the one the source generator writes, and it names the notification
classes that carry `[NotificationName]`:

```csharp
[NotificationName("orders.placed")]
public sealed record OrderPlaced(Guid OrderId, decimal Total) : INotification;
```

- The name identifies the stored payload, possibly for another process or a later version of the application, so keep
  it stable across refactors and unique across notification types. Two types with one name are **CQRGEN002** in one
  assembly and, across assemblies, **CQRGEN020** at build where one assembly composes them and **CQRCONF010** at run
  time, where a publish of the type that lost the name fails instead of silently staying in-process.
- `[NotificationName]` applies to classes and records. A `struct` notification always runs in-process.
- A handled notification without a name runs in-process even while an outbox mode is active; its first such publish
  logs **CQRCONF003** (so does the startup validator), and **CQRA020** suggests the attribute at build when the whole
  configuration is in view, so the bypass is not silent.
- Only the handlers the source generator discovered are outbox subscriptions. Handlers registered by hand in DI run for
  in-process publishes only; for a durable notification its first publish to the outbox logs **CQRCONF011**.

### Supported shapes

The generated serializer is written at compile time and works under Native AOT. A `[NotificationName]` type whose shape
it cannot handle fails the build with **CQRGEN005**, which names the member at fault. The same rules decide which
idempotent requests get an [automatic payload fingerprint](idempotency-and-resilience.md#key-reuse-with-a-different-payload).

Supported:

- `string`, `bool`, `char`, the integer and floating-point types, `decimal`, `Guid`, `DateTime`, `DateTimeOffset`,
  `TimeSpan`, `DateOnly`, `TimeOnly` and `Uri`.
- Enums, written as their underlying number.
- `Nullable<T>` of any of the above.
- Single-dimension arrays.
- `List<T>`, `IList<T>`, `IReadOnlyList<T>`, `ICollection<T>`, `IReadOnlyCollection<T>` and `IEnumerable<T>`, read back
  as `List<T>`.
- `HashSet<T>`, `ISet<T>` and `IReadOnlySet<T>`, written as a JSON array and read back as `HashSet<T>`.
- `Dictionary<string, T>`, `IDictionary<string, T>` and `IReadOnlyDictionary<string, T>`: a JSON object with the keys
  as written (no naming policy), read back as `Dictionary<string, T>`; the last of two duplicate keys wins.
- Non-abstract classes, structs and records of your own built from these.

Not supported: dictionaries with non-string keys, other `System.*` types, multi-dimensional arrays, abstract or
polymorphic members, and cycles.

The wire format matches System.Text.Json: a `char` is a one-character string, `DateOnly` is `yyyy-MM-dd`, `TimeOnly`
and `TimeSpan` use the `TimeSpan` `"c"` format (`13:45:30.1250000`), and a `Uri` is its `OriginalString`, read back with
`UriKind.RelativeOrAbsolute`. `[JsonPropertyName]` renames a member. Only an unconditional `[JsonIgnore]` (or one with
`Condition = Always`, `WhenWriting` or `WhenReading`) leaves a property out; `Never`, `WhenWritingNull` and
`WhenWritingDefault` members are written and read back.

A notification is read back through one constructor, chosen as System.Text.Json chooses it:

1. The one constructor marked `[JsonConstructor]`. It must be accessible, and every parameter must match a member by
   name and type.
2. Otherwise the parameterless constructor, when every member can be set.
3. Otherwise the one constructor with the most parameters, all of which match members. When two tie, mark one with
   `[JsonConstructor]`.

### Evolving a durable notification

A stored message is read by whatever version of the application claims it, so plan changes to a notification's shape:

- **Adding a member is safe.** A member missing from a stored payload keeps what the type's own construction gives it:
  its property initializer, or the default value of the constructor parameter it maps to
  (`record OrderPlaced(Guid Id, string Channel = "web")`).
- **Adding a `required` member is not.** A payload without it cannot be read, and the message is dead-lettered.
- **Removing or renaming a member** drops it from new payloads, unless `[JsonPropertyName]` keeps the old name.
- When a payload lacks a member, the type is constructed a second time to read its defaults, so side effects in a
  constructor or initializer run twice in that case.

### Custom notification serializers

To store notifications in another format, or types the generated serializer cannot handle, register your own:

```csharp
services.AddNotificationSerializer<MyNotificationSerializer>();
```

`AddNotificationSerializer<T>()` (namespace `CQRSharp`) registers `T` as the one serializer, a singleton, and replaces
the generated serializer completely, whether it runs before or after `AddCqrsGenerated`. From then on `[NotificationName]`
means nothing by itself: the custom serializer must name, serialize and deserialize every notification that should be
durable, the `[NotificationName]` ones included, because the generated serializers are internal and cannot be delegated
to. A notification without `[NotificationName]` has no `PartitionBy`; implement `IPartitionedNotification` to order it.

`INotificationSerializer` has three members:

| Member | Contract |
| --- | --- |
| `TryGetNotificationName(Type, out string?)` | `true` and the stable name for a durable type; `false` for any other, which then runs in-process. |
| `Serialize(INotification)` | The payload stored with each message. Called only for a type the serializer names. |
| `Deserialize(string, byte[])` | The notification, or `null` when this instance does not know the name. A payload that cannot be read must throw `System.Text.Json.JsonException`; any other exception counts as a failed delivery attempt and is retried. |

The processor treats the three outcomes of `Deserialize` differently: a `JsonException` is dead-lettered at once, since
no retry can fix it; `null` is [deferred](#unknown-notifications-and-handlers) for another instance; any other exception
is retried with back-off.

## How a publish reaches the store

While a request of the scope is running (its handler, its behaviors and the requests it awaits), a durable notification
it publishes is **buffered**, and the request settles it when it ends:

| How the request ends | What happens to what it buffered |
| --- | --- |
| It succeeds, without a unit of work (or inside a transaction someone else owns) | Stored when the request ends. |
| It succeeds in its own unit-of-work transaction, with a store that [joins the transaction](#stores) | Stored inside the transaction, just before the commit: atomic with the data, and rolled back with it. |
| It succeeds in its own unit-of-work transaction, with a store that does not join it | Stored right after a successful commit. A failed commit publishes nothing, and no message is claimable before its data is visible. |
| It throws, is canceled, or returns a failed `CommandResult` | Discarded: the notifications describe work that did not happen. |

A store that does not join the transaction has one window: a crash, or a store failure, in the instant after the commit
loses those notifications. A store failure there is logged as an error (event 4206, see
[Observability](observability.md#logging)) with the notification types, and the request still succeeds: running
committed work again would be worse.

The rules hold per request and per attempt:

- A retried request (`UseResilience`) stores the notifications of the attempt that succeeded, once.
- A nested request that its caller awaits settles with its caller; one that nobody awaited settles its own.
- Requests running side by side in one scope (`Task.WhenAll`, a `Send` while enumerating a stream) settle independently.
- A command that returns a failed result under `UnitOfWorkOptions.RollbackOnFailedResult = false` commits its work, and
  its notifications with it (see [Unit of work](unit-of-work.md#what-counts-as-failure)).

A publish from **outside any running request of the scope** (a controller, a hosted service, a stream's consumer between
items) has no request to settle it, so it is written straight to the store. With the EF Core store that write calls
`SaveChangesAsync` on the scoped context, which also saves every other change tracked on it: save or discard your own
changes before publishing, or publish from inside a transactional request.

A handler that the processor runs for an outbox delivery owns what it publishes in the same way; see
[The inbox](#the-inbox-effectively-once-delivery).

The message carries the W3C `traceparent` of the span that published it, so the delivery span is parented to it (see
[Observability](observability.md#trace-propagation)).

## One message per handler

Delivery state lives **per (notification, handler)**. Publishing `OrderPlaced` to three handlers stores three messages,
each addressed to one handler by its stable name, each with its own attempts, back-off and dead letter. A slow or broken
handler only ever affects its own message: its two healthy siblings run once and never again while the third retries.

```csharp
public sealed class UpdateInventory : INotificationHandler<OrderPlaced> { /* ... */ }  // "Shop.Orders.UpdateInventory"

[NotificationHandlerName("orders.notify-warehouse")]                                  // pinned
public sealed class NotifyWarehouse : INotificationHandler<OrderPlaced> { /* ... */ }
```

The **handler name** is what a stored message is addressed to, so it must not change while messages addressed to it may
still be in the store. The default is the handler type's namespace-qualified name, which changes when the type is renamed
or moved. Pin the name with `[NotificationHandlerName]` before refactoring a handler, or drain the outbox first. A message
addressed to a name no handler carries is [deferred](#unknown-notifications-and-handlers), then dead-lettered. Two handlers
may not share a name: **CQRGEN012** reports it within one assembly and **CQRCONF009** across assemblies.

The fan-out follows the notification's **runtime type** `R`, the same rule an in-process publish follows (see
[Notifications](notifications.md)): every generated handler declared for `R`, a base type or an interface of `R`, each
once, for its nearest declared type. So:

- A notification **nothing subscribes to** stores no message at all.
- A handler declared for a base type or interface (`INotificationHandler<INotification>`) gets its own message for every
  durable notification assignable to it.
- Fan-out is decided when the notification is published: a handler added later receives only what is published after
  it exists.
- `INotificationPipelineBehavior<T>` wraps **each delivery**, for the runtime type: with three handlers, a logging
  behavior logs three deliveries.

## Ordered delivery

The outbox makes no ordering promise by default: with several processors, `OrderPlaced` and `OrderCancelled` for one
order may be delivered in either order. Give a notification a **partition key** to order its deliveries:

```csharp
[NotificationName("orders.placed", PartitionBy = nameof(OrderId))]
public sealed record OrderPlaced(Guid OrderId, decimal Total) : INotification;

[NotificationName("orders.cancelled", PartitionBy = nameof(OrderId))]
public sealed record OrderCancelled(Guid OrderId) : INotification;
```

`PartitionBy` names a readable property (**CQRGEN011** when generated code cannot read it). Its value is the key: a
string as it is, anything else rendered with the invariant culture, and `null` for an unordered delivery. For a composed
or computed key, implement `IPartitionedNotification`; when a notification has both, the interface wins. The relational
stores hold keys of up to 256 characters.

```csharp
[NotificationName("tenant.ledger.posted")]
public sealed record LedgerPosted(string Tenant, long Account, decimal Amount) : INotification, IPartitionedNotification
{
    public string? PartitionKey => $"{Tenant}/{Account}";
}
```

The guarantee: **messages that share a partition key and a handler are delivered strictly in the order they were
stored.** A store never hands out the next message of a partition while an earlier one is still pending or in progress
(backing off, leased by another instance, or not claimed yet), and never hands out any message of a partition while one
of them is being delivered, so a requeued dead letter waits for the delivery in flight rather than running beside it.
Different keys, a `null` key, and different handlers of one key are independent, so a stuck order never delays the
others, and one handler's back-off never delays another handler.

Two edges are deliberate:

- A **dead-lettered** message is terminal and releases its partition: one poison message stops one delivery, not every
  later delivery for that key. The dead letter is the operator's signal.
- The order is the order the messages were **stored**. Two concurrent transactions that publish for one key commit in
  some order, and that is the order the outbox sees.

## The processor

`OutboxProcessorOptions` (namespace `CQRSharp`) tunes the background processor. Invalid values fail host start.

| Option | Default | Meaning |
| --- | --- | --- |
| `PollingInterval` | `5s` | How long the processor waits after a poll that found nothing due. |
| `BatchSize` | `100` | The most messages claimed at once. |
| `MaxDegreeOfParallelism` | `1` | How many messages of a batch are delivered at the same time, each in its own DI scope. A batch never holds two messages of one partition, so ordering holds at any degree. Raise it when handlers wait on I/O. |
| `MaxAttempts` | `3` | The total number of delivery attempts, the first included, before a failing message is dead-lettered: `3` means at most two retries, `1` means no retry. At least 1. The count is stored with the message, so it survives restarts. |
| `Retry` | 2 s × 2ⁿ, capped at 5 min, ±20 % jitter | The back-off between attempts (`OutboxRetryOptions`: `BaseDelay`, `BackoffMultiplier`, `MaxDelay`, `JitterFactor`; set `JitterFactor = 0` for deterministic tests). |
| `UnknownRecipientGracePeriod` | `1h` | How long a message this instance cannot deliver is left for other instances; see below. Greater than zero. |
| `UseInbox` | `true` | Deduplicates deliveries through the registered `IInboxStore`. |
| `BacklogSampleInterval` | `30s` | How often the backlog gauges are refreshed while something listens to them. |

**Draining.** While messages are due, the processor claims batch after batch without waiting, so a backlog, or the
next message of a partition whose head was just delivered, is delivered back to back. It waits for `PollingInterval`
only after a poll that found nothing due, and a message stored by this process wakes it at once (`IOutboxSignal`,
namespace `CQRSharp.Core.Outbox`). So the interval bounds how late the processor notices a message stored by another
instance, or one whose back-off or deferral has run out. A producer that writes to the store directly can wake it too:
resolve `IOutboxSignal` and call `Signal()` after the write commits.

**Delivery.** Each message is delivered to the one handler it is addressed to, in its own DI scope, so one handler's
scoped state (a `DbContext` with half-tracked entities after a failure) never reaches the next message. On success the
message is marked processed. A handler that throws counts one attempt: the message returns to pending with a back-off,
or is dead-lettered once `MaxAttempts` is reached. A step before the handler that fails (renewing the lease, checking
the inbox, beginning the delivery's transaction) is not the handler's failure and charges no attempt: the processor
logs event 5028 (Warning), counts the outcome `not_started`, and leaves the message under its lease, which serves as
its back-off, so it is claimable again once the lease runs out.

**Shutdown.** Once the host starts stopping, the processor starts no further message of its batch and hands the rest
back to the store without counting an attempt, so a restart does not wait out their leases. The outcome of a delivery
that finished is still recorded during shutdown.

### Unknown notifications and handlers

During a rolling deploy, an instance on the previous version can claim a message that only a newer instance can deliver:
a notification name its serializer does not know, or a handler name nothing subscribes under. Such a message is
**deferred**, not failed: it goes back to the store without counting an attempt, to be claimed again later, first after
`PollingInterval` and then after longer delays as the message ages, up to `Retry.MaxDelay`. Once
`UnknownRecipientGracePeriod` has passed since the message was created and no instance has delivered it, it is
dead-lettered with an error that names the grace period and, for a handler, the `[NotificationHandlerName]` advice.

- Adding a durable notification or a handler is safe when the rollout completes within the grace period. A canary that
  runs next to the old version for longer needs a longer period.
- Removing or renaming a handler still needs a drain, or a pinned name.

## The inbox: effectively-once delivery

The outbox is at-least-once: a process can crash between the handler and the processed mark, and recording the outcome
can itself fail, in which case the message is delivered again once its lease runs out. The **inbox** (`IInboxStore`,
namespace `CQRSharp.Persistence`) records every completed delivery, the (message, handler) pair, so a redelivery is
recognised and skipped instead of running the handler twice. Every built-in store registers its inbox with the outbox
store, and the processor uses it whenever one is registered and `UseInbox` is on. A skipped redelivery is marked
processed and counted as `duplicate` on the outbox metrics.

When the processor uses an inbox and the delivery's scope has an `IUnitOfWork` with no transaction open, it runs the
delivery in a transaction of its own: it begins one (at the data store's default isolation level), runs the handler, and
commits. A handler that begins its own transaction on that unit of work must check `HasActiveTransaction` first; a
transactional request it sends takes part in the delivery's transaction on its own. Then:

| Inbox | Guarantee |
| --- | --- |
| One that joins the transaction: the EF Core inbox, with `UseEntityFrameworkCoreUnitOfWork<TContext>()` over the same `DbContext` | **Exactly once for what the handler writes through that context.** The record is written inside the transaction, so the handler's changes and the record are one commit. A failing handler rolls both back, and a redelivery racing the original loses at record time and takes its handler's changes with it. |
| Any other combination: no unit of work, a unit of work over another context, the Redis or in-memory inbox | **At least once, with duplicates confined to two windows.** The record is written right after the handler's work stands. A duplicate can come from a crash, or a failed record, between the handler and the record; or from a lease that expires while a slow handler is still running, so another processor delivers the message too. Keep the visibility timeout well above the slowest handler. |

A record outside the processor's own transaction is bookkeeping about work that already stands: it is written even
while the host is stopping, and when it fails the processor logs a warning (event 5023) and counts the delivery as
delivered, never as a failed attempt. The processor renews a message's lease just before dispatching it, never while
its handler runs.

**What a delivery publishes** belongs to the delivery, as a request's notifications belong to the request. It is
buffered while the handler runs, stored once the delivery succeeds, and discarded when the handler throws, the commit
fails, or a joining inbox reports the delivery as a duplicate, so a retried delivery publishes once. In the processor's
own transaction, a store that joins it is written inside the transaction and any other store right after the commit;
if that write fails after the commit, the notifications are lost and logged as an error (event 5027). Without a
processor transaction, the publishes are stored before the inbox record, so a crash in between causes a redelivery,
never a lost notification.

> The EF Core inbox never saves a handler's changes on its behalf. A handler that runs without a unit of work calls
> `SaveChangesAsync` itself; if the context still holds unsaved changes when the delivery is recorded outside a
> transaction, the record is refused (logged as event 5023) and those changes are discarded with the delivery's scope.

Records age out after the store's `InboxRetention` (7 days by default), which must comfortably exceed the visibility
timeout.

## Dead letters

A message that used up its attempts, whose payload cannot be read, or whose notification or handler no instance knew
within the grace period is **dead-lettered**: kept with its payload, its `LastError`, its `AttemptCount` and the time it
failed (`FailedAt`), and counted on the gauges and the health check. `IOutboxStore` gives an operator three tools for
it:

```csharp
using CQRSharp.Persistence;

var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

foreach (var dead in await store.GetDeadLettersAsync(limit: 50, ct))                 // oldest first
    Console.WriteLine($"{dead.NotificationType} to {dead.HandlerName} failed at {dead.FailedAt}: {dead.LastError}");

await store.RequeueAsync(messageId, ct);                                              // a fresh budget, once the cause is fixed
await store.PurgeDeadLettersAsync(failedBefore: DateTime.UtcNow.AddDays(-30), ct);   // delete what nobody will requeue
```

A requeued message returns to pending with zero attempts, no back-off and no failure time (its `LastError` is kept as
the record), and takes its place in its partition again by creation time. Dead letters are kept until they are requeued
or purged, in every store; set the store's `DeadLetterRetention` to have old ones deleted automatically.

## Backlog, gauges and the health check

`IOutboxStore.GetBacklogAsync` measures the outbox: how many messages are still to be delivered (pending, backing off or
in progress), how many are dead-lettered, and how old the oldest undelivered one is. Two consumers are built in:

- **Gauges.** The processor refreshes the `cqrsharp.outbox.pending`, `cqrsharp.outbox.dead_letters` and
  `cqrsharp.outbox.lag` gauges every `BacklogSampleInterval` while a listener is attached, so an idle meter never costs a
  query. See [Observability](observability.md#dispatch-metrics). Alert on lag and on dead letters.
- **The health check.** `AddCqrsOutbox()` measures the backlog live on every probe. It reports *degraded* when the
  oldest undelivered message is older than `MaxLag` (default 5 minutes; `null` never degrades on lag) or there are more
  dead letters than `MaxDeadLetters` (default 0; `null` never degrades on dead letters), and the registration's failure
  status (default `Unhealthy`) when the store cannot be read. A disabled outbox is healthy. The result's data carries
  `pending`, `deadLetters`, `lagSeconds` and, when something is pending, `oldestPendingCreatedAt`.

```csharp
using CQRSharp.Core.Diagnostics.HealthChecks;

services.AddHealthChecks().AddCqrsOutbox(configure: o =>
{
    o.MaxLag = TimeSpan.FromMinutes(2);
    o.MaxDeadLetters = 10;
});
```

`AddCqrsOutbox(name = "cqrsharp.outbox", failureStatus = null, tags = null, configure = null)` keeps its thresholds
per registration: they are the `OutboxHealthCheckOptions` named after the check, so a liveness and a readiness check
under different names keep their own. Bind them from configuration with
`services.Configure<OutboxHealthCheckOptions>(name, section)`. Negative thresholds fail host start.

## Stores

The store is the durable boundary: in-memory (built in, not durable), Redis (`CQRSharp.Redis`) or EF Core
(`CQRSharp.EntityFrameworkCore`). [Integrations](integrations.md#choosing-a-store) compares them, including which ones
join the unit of work's transaction and so make the outbox atomic with your data.

## Custom stores

Implement `IOutboxStore` (namespace `CQRSharp.Persistence`) to back the outbox with any storage, together with an
`IInboxStore`, and register the pair through the builder hook:

```csharp
.UseOutbox(o => o.UseStore(s =>
{
    s.AddSingleton<IOutboxStore, MyOutboxStore>();
    s.AddSingleton<IInboxStore, MyInboxStore>();
}))
```

A store must:

- report `JoinsUnitOfWork` honestly: `true` only while a write made now goes through the transaction the scope's
  `IUnitOfWork` has open, and `false` otherwise (always `false` for a store on another connection or database);
- claim atomically (`ClaimPendingAsync`: `Pending` to `InProgress`), so concurrent processors never claim one message
  twice;
- claim oldest first by `CreatedAt`, then in the order stored, and honour the [partition rule](#ordered-delivery);
- implement the dead-letter operations and measure the backlog.

Verify it against the contract suites in `CQRSharp.Testing.Xunit.V3` before relying on it; see
[The testing packages](testing-package.md#contract-testing-an-outbox-store).

### Claims and leases

Every message `ClaimPendingAsync` hands out comes as a `ClaimedOutboxMessage`, which pairs the `OutboxMessage` with an
`OutboxClaim`: the message id, an opaque token and `LeasedUntil`. Build it as
`new ClaimedOutboxMessage(message, new OutboxClaim(id, token, leasedUntil))`. The claim is the processor's proof that it
still holds the message, and **every later operation presents it**: `MarkAsProcessedAsync`, `IncrementAttemptAsync`,
`MarkAsFailedAsync`, `DeferAsync`, `RenewAsync` and `ReleaseAsync`.

A message left in progress past its lease (a crashed or stalled processor) is handed out again under a *new* claim, and
the old one stops matching. When the stalled processor reports in, its operation changes nothing and returns `false`,
`0` or `null`: it cannot move a message another processor is working on back to pending, nor overwrite that processor's
outcome. That, not a lock, is what keeps several processors safe.

- `IncrementAttemptAsync(claim, error, nextRetryAt)` records a failed attempt: it increments `AttemptCount`, stores the
  error, sets `NextRetryAt` and returns the message to pending.
- `MarkAsFailedAsync(claim, error)` dead-letters the attempt that exhausts the budget: it increments `AttemptCount`,
  stores the error, sets `FailedAt` and clears `NextRetryAt`.
- `DeferAsync(claim, notBefore, reason)` returns the message to pending with `NextRetryAt = notBefore` and the reason as
  `LastError`, without touching `AttemptCount`.
- `RenewAsync(claim)` extends the lease by the visibility timeout. The processor calls it just before dispatching a
  message once half of its lease has passed. A renewal is refused (`null`) once the lease has run out.
- `ReleaseAsync(claims)` gives undispatched messages back at once without counting an attempt; the processor calls it on
  shutdown.

## At-least-once and idempotent handlers

Unless the inbox joins the delivery's transaction, a message may be delivered more than once (see
[the inbox](#the-inbox-effectively-once-delivery)). Design notification handlers reached through the outbox to be
**idempotent**: safe to run twice with the same effect.
