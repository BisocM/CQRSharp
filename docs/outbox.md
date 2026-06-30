# The outbox

The **transactional outbox** gives notifications **at-least-once delivery** that survives crashes and
commits **atomically** with your business transaction. Instead of publishing a notification in-process
(where a crash after the DB commit but before delivery loses the event), the notification is written to
an outbox table in the *same* transaction as your data, and a background processor delivers it
afterward.

- [Why an outbox](#why-an-outbox)
- [Outbox modes](#outbox-modes)
- [Enabling the outbox](#enabling-the-outbox)
- [The durability contract](#the-durability-contract)
- [How it works](#how-it-works)
- [The outbox message](#the-outbox-message)
- [The processor](#the-processor)
- [Stores](#stores)
- [At-least-once and idempotent handlers](#at-least-once-and-idempotent-handlers)
- [Custom stores](#custom-stores)

## Why an outbox

The classic *dual-write* problem: you commit a database change **and** publish an event. If the process
dies between the two, the system is inconsistent — the data changed but no one was told, or the event
fired but the data didn't commit. The outbox collapses both writes into one transaction: the event is
persisted *with* the data, then delivered out-of-band with retries.

## Outbox modes

`OutboxMode` (on `OutboxOptions`) has three values:

| Mode | Behavior |
| --- | --- |
| `Disabled` *(default)* | The outbox is off; `Publish` always dispatches in-process. |
| `Enabled` | **Every** published notification is routed through the outbox for deferred processing. |
| `Transactional` | A notification is routed through the outbox **only when published inside an active unit-of-work transaction**; published outside one, it dispatches directly in-process. |

`Transactional` is the mode you usually want with a database: events raised while handling a
transactional command are captured and committed atomically; events raised elsewhere still work,
in-process.

## Enabling the outbox

The outbox is off by default. Enable it in one cohesive step with `UseOutbox`, which selects the mode,
registers a store, and ensures the processor runs:

```csharp
services.AddCqrsGenerated(b => b
    .UseOutbox(o => o
        .Transactional()                      // mode: Transactional (default) or Enabled()
        .UseEntityFrameworkCore<AppDbContext>() // store: durable
        .ConfigureProcessor(p =>
        {
            p.PollingInterval  = TimeSpan.FromSeconds(2);
            p.BatchSize        = 200;
            p.MaxRetryAttempts = 5;
        })));
```

`OutboxStoreBuilder` verbs:

| Verb | Effect |
| --- | --- |
| `Transactional()` *(default)* | Set mode to `Transactional`. |
| `Enabled()` | Set mode to `Enabled`. |
| `UseInMemoryStore(configure?)` | Non-durable in-memory store — development/tests/single-node demos only; messages are lost on restart. |
| `UseRedis(...)` | Durable Redis store (from `CQRSharp.Redis`). |
| `UseEntityFrameworkCore<TContext>()` | Durable EF Core store (from `CQRSharp.EntityFrameworkCore`). |
| `UseStore(Action<IServiceCollection>)` | Register any custom `IOutboxStore`. The hook the integration packages build on. |
| `ConfigureProcessor(Action<OutboxProcessorOptions>)` | Tune polling interval, batch size, retry budget. |

A bare `.UseOutbox(o => { })` uses the in-memory store and is a working development configuration.

## The durability contract

A notification can be carried by the outbox **only if it has a stable name**:

```csharp
[NotificationName("orders.placed")]
public sealed record OrderPlaced(Guid OrderId, decimal Total) : INotification;
```

- `[NotificationName("...")]` supplies a stable identifier used to serialize and later deserialize the
  notification — keep it stable across refactors.
- The notification's properties must be of **generator-serializable shapes** (scalars, nested objects,
  and collections thereof). The generator emits an AOT-safe serializer for them; if a `[NotificationName]`
  type isn't serializable, it reports **CQRGEN005**.
- A handled notification **without** `[NotificationName]` cannot be persisted, so it dispatches
  in-process even when an outbox mode is enabled. The startup validator surfaces this as **CQRCONF003**
  so it isn't a silent surprise.

## How it works

1. While a transactional request is handled, calling `Publish` adds the notification to an in-memory
   `IOutbox` buffer rather than dispatching it.
2. When the unit of work commits, the framework **drains** the buffer, serializes each notification into
   an `OutboxMessage`, and calls `IOutboxStore.StoreAsync(...)` **inside the same transaction** — so the
   messages commit atomically with your data.
3. The background **`OutboxProcessor`** polls the store on an interval. Each cycle it **atomically
   claims** a batch of due messages (`GetPendingAsync`, transitioning them `Pending → InProgress` so two
   processors never grab the same message), deserializes each, and dispatches it to its handlers.
4. On success the message is marked **processed** (`MarkAsProcessedAsync`). On failure the attempt is
   recorded (`IncrementAttemptAsync`) with a **persisted attempt count** and a `NextRetryAt` back-off,
   returning the message to `Pending` for a later retry. When attempts are exhausted, the message is
   **dead-lettered** (`MarkAsFailedAsync`).

The W3C `traceparent` of the originating request is captured on the message, so the outbox dispatch
span links back to the request that produced it.

## The outbox message

```csharp
public sealed record OutboxMessage(
    Guid    Id,
    string  NotificationType,   // the stable [NotificationName]
    byte[]  Payload,            // the serialized notification
    DateTime CreatedAt,
    OutboxMessageStatus Status, // Pending -> InProgress -> Processed / Failed
    DateTime? ProcessedAt,
    string?   LastError,
    int       AttemptCount = 0, // persisted, so retry limits survive restarts
    DateTime? NextRetryAt = null,
    string?   TraceParent = null);
```

## The processor

`OutboxProcessorOptions` tunes the background processor:

| Option | Default | Meaning |
| --- | --- | --- |
| `PollingInterval` | `5s` | How often the store is polled for due messages. |
| `BatchSize` | `100` | Maximum messages claimed per cycle. |
| `MaxRetryAttempts` | `3` | Failed-delivery attempts before a message is dead-lettered. |

## Stores

The store is the durable boundary. Pick one to match your deployment:

| Store | Package | Durable | AOT |
| --- | --- | --- | --- |
| In-memory | `CQRSharp.Core` (built-in) | No | Yes |
| Redis | `CQRSharp.Redis` | Yes — atomic server-side Lua claim/lease | Yes |
| EF Core (relational) | `CQRSharp.EntityFrameworkCore` | Yes — optimistic-concurrency claim lease | No¹ |

¹ EF Core uses runtime query compilation, so the EF store is not Native-AOT/full-trim compatible. See
[Native AOT](native-aot.md) and [Integrations](integrations.md).

`IOutboxStore` implementations must make `StoreAsync` atomic with the business transaction and make
`GetPendingAsync` an atomic claim (`Pending → InProgress`) so concurrent processors never double-claim.

## At-least-once and idempotent handlers

The outbox is **at-least-once**: a message may be delivered more than once (e.g. a crash after dispatch
but before `MarkAsProcessedAsync`). Design notification handlers reached through the outbox to be
**idempotent** — safe to run twice with the same effect. See
[Idempotency & resilience](idempotency-and-resilience.md).

## Custom stores

Implement `IOutboxStore` to back the outbox with any storage. Register it through the builder hook:

```csharp
.UseOutbox(o => o.Transactional().UseStore(s => s.AddSingleton<IOutboxStore, MyStore>()))
```

Verify your implementation against the shared **store contract tests** before relying on it — see
[Testing](testing.md). The contract covers atomic claiming, retry/back-off, dead-lettering, and
concurrent-claim safety.
