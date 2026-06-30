# Notifications

A **notification** is a one-to-many event: you publish it, and every registered handler receives it.
Notifications never return a value. CQRSharp gives you a publish API, configurable fan-out strategies,
a set of automatic **lifecycle notifications**, a notification middleware pipeline, and an optional
**durable** path through the transactional outbox.

- [Defining and handling notifications](#defining-and-handling-notifications)
- [Publishing](#publishing)
- [Publish strategies](#publish-strategies)
- [Failure semantics](#failure-semantics)
- [Lifecycle notifications](#lifecycle-notifications)
- [Notification pipeline behaviors](#notification-pipeline-behaviors)
- [Durable notifications (the outbox)](#durable-notifications-the-outbox)

## Defining and handling notifications

Implement `INotification` on the event, and `INotificationHandler<TNotification>` on each subscriber:

```csharp
public sealed record OrderPlaced(Guid OrderId, decimal Total) : INotification;

public sealed class UpdateInventory : INotificationHandler<OrderPlaced>
{
    public Task Handle(OrderPlaced n, CancellationToken ct) => /* ... */ Task.CompletedTask;
}

public sealed class NotifyWarehouse : INotificationHandler<OrderPlaced>
{
    public Task Handle(OrderPlaced n, CancellationToken ct) => /* ... */ Task.CompletedTask;
}
```

The source generator discovers every handler. A notification may have **zero** handlers (publishing is
then a no-op) — that's legal, but often unintended, so the **CQRA006** analyzer surfaces it as an info
diagnostic.

## Publishing

Publish through the dispatcher:

```csharp
await cqrs.Publish(new OrderPlaced(orderId, total));
```

`Publish<TNotification>(TNotification, CancellationToken)` resolves all handlers for the notification
from the **current DI scope** and invokes them according to the configured publish strategy.

## Publish strategies

All of a notification's handlers run in the **same DI scope** (the scope that published the
notification). How they run is controlled by `PublishStrategy`, configured on `NotificationOptions`:

| Strategy | Behavior |
| --- | --- |
| `Sequential` *(default)* | Invoke handlers one at a time, in order, awaiting each before the next. Stops at the first failure. |
| `Parallel` | Start every handler concurrently and await them all; the first failure surfaces. |
| `ParallelWhenAllAggregate` | Start every handler concurrently and await them all, aggregating **every** failure into an `AggregateException`. |

`Sequential` is the default **on purpose**: because handlers share the dispatching scope, a parallel
strategy would run them concurrently against any non-thread-safe scoped service they share — most
notably an EF Core `DbContext`. Opt into a parallel strategy only when your handlers are independent.

```csharp
services.AddCqrsGenerated(b => b
    .ConfigureNotifications(o => o.PublishStrategy = PublishStrategy.ParallelWhenAllAggregate));
```

See [Configuration](configuration.md#notificationoptions) for `ConfigureNotifications`.

## Failure semantics

- **`Sequential`** is fail-fast: the first handler that throws stops the run; later handlers do not
  execute.
- **`Parallel`** and **`ParallelWhenAllAggregate`** start *every* handler before observing failures, so
  a handler that throws does not prevent its siblings from running. A handler that throws
  **synchronously** (before returning its `Task`) is isolated — it is captured as a faulted task and
  surfaced with the rest rather than abandoning siblings that already started.
- `Parallel` rethrows the first failure; `ParallelWhenAllAggregate` rethrows an `AggregateException`
  carrying all of them.

## Lifecycle notifications

The dispatcher automatically publishes notifications around every request it processes, so you can
observe command/query/stream execution by simply handling them — no interception required:

| Notification | Published | Key members |
| --- | --- | --- |
| `CommandInitiatedNotification` | before a command's handler runs | `Command`, `CommandName` |
| `CommandCompletedNotification` | after a command's handler succeeds | `Command`, `CommandName`, `Result` |
| `CommandFailedNotification` | when a command's handler throws | `Command`, `CommandName`, `Exception` |
| `QueryInitiatedNotification` | before a query's handler runs | `Query`, `QueryName` |
| `QueryCompletedNotification<TResult>` | after a query succeeds | `Query`, `QueryName`, `Result` (`object?`) |
| `QueryFailedNotification` | when a query's handler throws | `Query`, `QueryName`, `Exception` |
| `StreamInitiatedNotification` | before a stream's handler runs | request, name |
| `StreamCompletedNotification` | after a stream finishes | request, name |
| `StreamFailedNotification` | when a stream faults | request, name, exception |
| `TaskEnqueuedNotification` | when a dispatch is queued (`RunMode.Queued`) | the queued work |
| `TaskRejectedNotification` | when the background queue rejects a dispatch (back-pressure) | the rejected work |

For a given execution, after the *Initiated* notification exactly one of *Completed* (success) or
*Failed* (the handler threw) is published.

```csharp
// Centralized audit of every command outcome.
public sealed class CommandAuditor : INotificationHandler<CommandCompletedNotification>
{
    public Task Handle(CommandCompletedNotification n, CancellationToken ct)
    {
        _log.LogInformation("{Command} -> {Outcome}", n.CommandName,
            n.Result.IsSuccess ? "ok" : n.Result.ErrorMessage);
        return Task.CompletedTask;
    }
}
```

> `QueryCompletedNotification<TResult>.Result` is typed as `object?`; cast it to the query's result
> type when you need the value.

### Observing *every* notification

A handler like `CommandAuditor` above is closed over **one** notification type. To audit **every**
notification with a single type, you might reach for an open-generic handler
(`AuditAll<TN> : INotificationHandler<TN>`) — but **that does not work**: the source generator wires
only **closed** notification handlers, so an open-generic `INotificationHandler<TN>` is never registered
and receives nothing. The generator flags it with **CQRGEN009** (the same diagnostic it raises for any
open-generic dispatch handler).

The correct centralized "audit every notification" hook is an open-generic
[**notification pipeline behavior**](#notification-pipeline-behaviors) —
`INotificationPipelineBehavior<TN>` — which the runtime resolves per published notification and which
*does* support the open generic. The Sample's `NotificationLoggingBehavior<TNotification>` demonstrates
exactly this: one open-generic behavior that logs and times the fan-out of every notification (lifecycle
notifications included). See [Notification pipeline behaviors](#notification-pipeline-behaviors) below.

## Notification pipeline behaviors

Just as requests have a behavior pipeline, notifications have one too. Implement
`INotificationPipelineBehavior<TNotification>` to wrap the fan-out of a notification — for logging,
tracing, suppression, or enrichment:

```csharp
public interface INotificationPipelineBehavior<in TNotification>
    where TNotification : INotification
{
    Task Handle(TNotification notification, Func<CancellationToken, Task> next, CancellationToken ct);
}
```

```csharp
public sealed class TraceNotifications<TN> : INotificationPipelineBehavior<TN>
    where TN : INotification
{
    public async Task Handle(TN n, Func<CancellationToken, Task> next, CancellationToken ct)
    {
        using var _ = StartActivity(n.GetType().Name);
        await next(ct);   // run the handlers (or the next behavior)
    }
}
```

Register notification behaviors in DI (open or closed generic). When more than one applies, they run
ordered by priority (implement `IPrioritizedPipelineBehavior` to set it), then by type name. The
behaviors wrap the handler fan-out; the configured publish strategy still governs how the handlers
themselves run.

## Durable notifications (the outbox)

By default, `Publish` dispatches **in-process**: handlers run within the publishing scope and the call
completes when they do. For at-least-once delivery that survives a crash or commits atomically with
your database transaction, route the notification through the **transactional outbox**.

A notification is eligible for the outbox only if it has a stable name:

```csharp
[NotificationName("orders.placed")]   // stable across refactors — this is the durability opt-in
public sealed record OrderPlaced(Guid OrderId, decimal Total) : INotification;
```

`[NotificationName]` provides a stable identifier used to serialize the notification for durable
storage. Without it, the notification cannot be persisted and is dispatched in-process even when an
outbox is enabled — and the startup validator reports this as **CQRCONF003** so it isn't a silent
surprise. The notification's properties must also be of generator-serializable shapes (the generator
reports **CQRGEN005** otherwise).

See [The outbox](outbox.md) for enabling the outbox, choosing a store, and the transactional-commit
semantics.
