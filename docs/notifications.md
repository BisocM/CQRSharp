# Notifications

A **notification** is a one-to-many event: you publish it, and each of its handlers receives it. Notifications return
nothing. CQRSharp provides the publish API, fan-out strategies, **lifecycle notifications** around every request, a
notification pipeline, and an optional **durable** path through the outbox.

- [Defining and handling notifications](#defining-and-handling-notifications)
- [Which handlers a notification reaches](#which-handlers-a-notification-reaches)
- [Publish strategies](#publish-strategies)
- [Lifecycle notifications](#lifecycle-notifications)
- [Notification pipeline behaviors](#notification-pipeline-behaviors)
- [Durable notifications](#durable-notifications)

## Defining and handling notifications

Implement `INotification` on the event, and `INotificationHandler<TNotification>` on each subscriber:

```csharp
public sealed record OrderPlaced(Guid OrderId, decimal Total) : INotification;

public sealed class UpdateInventory : INotificationHandler<OrderPlaced>
{
    public Task Handle(OrderPlaced notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class NotifyWarehouse : INotificationHandler<OrderPlaced>
{
    public Task Handle(OrderPlaced notification, CancellationToken cancellationToken) => Task.CompletedTask;
}
```

Publish through the dispatcher:

```csharp
await cqrs.Publish(new OrderPlaced(orderId, total));
```

Notifications can be classes, records or structs. The source generator discovers every closed notification handler and
registers it; an open-generic handler (`AuditAll<T> : INotificationHandler<T>`) is not registered and receives nothing
(**CQRGEN009**). Publishing a notification that has no handler is legal and does nothing beyond running its
[behaviors](#notification-pipeline-behaviors); because that is often a mistake, the **CQRA006** analyzer reports it as an
info diagnostic where it is published.

## Which handlers a notification reaches

A published notification of runtime type `R` reaches:

- every generated handler declared for `R`, for a base class of `R`, or for an interface `R` implements, each once;
- the handlers registered by hand in the container as `INotificationHandler<R>`.

The set is the same whether `R` is published as itself, as a base type or as `INotification` (a list of domain events
typed `INotification`). A handler for a base type or an interface therefore also runs for a derived notification that
has handlers of its own.
(A runtime type no generated module knows, because it is declared and handled only where the generator does not run, is
delivered as the type it was published as.)

Generated handlers are registered by their concrete type only, not as `INotificationHandler<T>`: the container's
`INotificationHandler<T>` registrations are the ones the application makes itself, and resolving
`IEnumerable<INotificationHandler<T>>` returns only those; the generated subscriptions are internal to the runtime. A
generated handler that is also registered by hand (by an assembly scan, say) runs once.

All of a notification's handlers run in the **DI scope that published it**;
[`ExecutionScopeMode`](configuration.md#scopemode) applies to requests, not to publishing. A publish from a request handler
that runs in a scope of its own uses that scope.

## Publish strategies

How the handlers run is set by `NotificationOptions.PublishStrategy`:

| Strategy | Runs the handlers | On failure |
| --- | --- | --- |
| `Sequential` *(default)* | One at a time, each awaited before the next: the generated handlers ordered by their stable handler name, then the handlers registered by hand in registration order. | Stops at the first failure; later handlers do not run. The exception propagates. |
| `Parallel` | All started at once, then awaited together. | Every handler runs. The first failure in handler order propagates; the others are observed but not surfaced. |
| `ParallelWhenAllAggregate` | All started at once, then awaited together. | Every handler runs. With more than one failure, an `AggregateException` carrying all of them is thrown; a single failure is rethrown as itself. |

`Sequential` is the default because the handlers share the publishing scope: a parallel strategy runs them concurrently
against any scoped service they share that is not thread-safe, such as an EF Core `DbContext`. Choose a parallel strategy
only when the handlers are independent. Under a parallel strategy a handler that throws before returning its `Task` is
reported like one whose task faults, so the handlers already started still run to completion.

```csharp
services.AddCqrsGenerated(b => b
    .ConfigureNotifications(o => o.PublishStrategy = PublishStrategy.ParallelWhenAllAggregate));
```

An undefined `PublishStrategy` value (a number bound from configuration, say) fails host start with
`OptionsValidationException`.

## Lifecycle notifications

The dispatcher publishes notifications around the requests it runs, so you can observe every command, query and stream by
handling them. They are pay-for-use: a lifecycle notification is only created and published when the container has a
handler or a notification behavior that would receive it, so an application that subscribes to none pays nothing per
request.

| Notification | Published | Members |
| --- | --- | --- |
| `CommandInitiatedNotification` | before a command's pre-handlers and handler run | `Command`, `CommandName` |
| `CommandCompletedNotification` | after the handler and every post-handler succeeded | `Command`, `CommandName`, `Result` |
| `CommandFailedNotification` | when the command failed | `Command`, `CommandName`, `Exception` |
| `QueryInitiatedNotification<TResult>` | before a query's pre-handlers and handler run | `Query`, `QueryName` |
| `QueryCompletedNotification<TResult>` | after the handler and every post-handler succeeded | `Query`, `QueryName`, `Result` (`TResult`) |
| `QueryFailedNotification<TResult>` | when the query failed | `Query`, `QueryName`, `Exception` |
| `StreamInitiatedNotification<TItem>` | when the consumer asks for the first item, before the pre-handlers run | `Request`, `RequestName` |
| `StreamCompletedNotification<TItem>` | after the stream was enumerated to its end and every post-handler succeeded | `Request`, `RequestName`, `ItemsYielded` |
| `StreamFailedNotification<TItem>` | when the stream failed | `Request`, `RequestName`, `ItemsYielded`, `Exception` |

- **Every command publishes the command notifications**, a value-returning one (`ICommand<TResult>`) included. `Command`
  is typed `ICommandMarker`; pattern-match (`notification.Command is CreateUser create`) to reach the command. `Result` is
  the command's `CommandResult`; for a value-returning command it is the `CommandResult<TResult>` instance, typed as
  `CommandResult`.
- **The query and stream notifications are closed over the result or item type.** Subscribe with
  `INotificationHandler<QueryCompletedNotification<UserDto>>` or
  `INotificationHandler<StreamCompletedNotification<Row>>`.
- **Exactly one terminal notification follows a delivered *Initiated*.** *Completed* means the handler and every post-handler
  succeeded; *Failed* covers every other ending: a pre-handler, the handler or a post-handler threw, the request was
  cancelled (by its caller's token or any other), or the timeout behavior's deadline passed. A command whose handler
  *returns* a failed `CommandResult` still completes: the result says it failed.
- ***Initiated* is published before the request's work begins**, with the request's token. A subscriber that throws
  (or a token already cancelled) fails the request with that exception before any pre-handler runs: no terminal
  notification follows and no post-handler runs, although subscribers that ran before it did receive *Initiated*.
- **The one exception is a stream whose consumer stops enumerating early** without an error: it publishes neither
  terminal notification (and runs no post-handlers).
- ***Failed* is published without a cancellation token**, since the request's own token may be the one that was
  cancelled. A *Failed* subscriber that throws is logged (event 1000, Warning) and never replaces the exception the
  caller receives. A *Completed* subscriber that throws fails the request instead: the caller receives that exception, and
  no *Failed* follows.
- **They are published inside the pipeline**, around the pre-handlers and the handler. A request rejected by an outer
  behavior (validation, rate limiting) publishes none, and a request the resilience behavior retries publishes them for
  every attempt.

```csharp
// An audit of every command outcome.
public sealed class CommandAuditor(ILogger<CommandAuditor> logger) : INotificationHandler<CommandCompletedNotification>
{
    public Task Handle(CommandCompletedNotification notification, CancellationToken cancellationToken)
    {
        logger.LogInformation("{Command} -> {Outcome}", notification.CommandName,
            notification.Result.IsSuccess ? "ok" : notification.Result.ErrorMessage);
        return Task.CompletedTask;
    }
}
```

To observe *every* notification with one type, an open-generic handler does not work (the generator registers closed
handlers only). Use an open-generic [notification pipeline behavior](#notification-pipeline-behaviors), which runs for
each notification type, lifecycle notifications included. The sample's `NotificationLoggingBehavior<TNotification>` does
this.

## Notification pipeline behaviors

Notifications have a behavior pipeline, like requests. Implement `INotificationPipelineBehavior<TNotification>`
(`CQRSharp.Pipelines`) to wrap the delivery of a notification, for logging, tracing, suppression or enrichment:

```csharp
public delegate Task NotificationHandlerDelegate(CancellationToken cancellationToken = default);

public interface INotificationPipelineBehavior<in TNotification>
    where TNotification : INotification
{
    Task Handle(TNotification notification, NotificationHandlerDelegate next, CancellationToken cancellationToken);
}
```

```csharp
public sealed class TraceNotifications<TNotification> : INotificationPipelineBehavior<TNotification>
    where TNotification : INotification
{
    private static readonly ActivitySource Source = new("MyApp.Notifications");

    public async Task Handle(TNotification notification, NotificationHandlerDelegate next, CancellationToken cancellationToken)
    {
        using var activity = Source.StartActivity(notification.GetType().Name);
        await next();   // the next behavior, or the delivery to the handlers; next(token) passes another token
    }
}

services.AddTransient(typeof(INotificationPipelineBehavior<>), typeof(TraceNotifications<>));
```

(`ActivitySource` is in `System.Diagnostics`.)

- **Open-generic behaviors are registered by you**, as above. **Closed behaviors are registered by the generator:** a
  public or internal, non-generic class that implements `INotificationPipelineBehavior<OrderPlaced>` is registered like a
  closed request behavior and runs without a registration call; one you also register yourself runs once, as your
  registration. A closed behavior for a lifecycle notification counts as a subscriber, so that notification is published.
- **They are resolved for the notification's runtime type.** A closed `INotificationPipelineBehavior<OrderPlaced>` runs
  for every `OrderPlaced`, however it was published, and an open-generic behavior is closed over the runtime type.
- **An in-process publish runs them once around the whole fan-out**, also when nothing handles the notification; the
  publish strategy still decides how the handlers inside run. An outbox delivery runs them around its one handler.
- **Order:** by priority (implement `IPrioritizedPipelineBehavior`; lower runs first, outermost), then by full type name.

Under Native AOT an open-generic notification behavior wraps a struct notification only through a closed factory the
generator emits; see [Native AOT](native-aot.md#value-type-results-and-notifications).

## Durable notifications

By default `Publish` delivers **in process**: the handlers run in the publishing scope, and the call completes when they
do. For delivery that survives a crash, turn on the [outbox](outbox.md) (`UseOutbox(...)`): notifications are stored and a
background processor delivers them, and with a store that joins your unit of work they are stored in the same transaction
as your data.

While the outbox is on, the registered `INotificationSerializer` decides which notifications go through it: those it can
name. With the generated serializer, that is the classes and records marked `[NotificationName]` whose properties it can
serialize (it reports **CQRGEN005** for one it cannot); struct notifications cannot carry the attribute and are always
delivered in process. Every other notification is delivered in process, and the startup validator reports a handled
notification that bypasses the outbox as **CQRCONF003**.

```csharp
[NotificationName("orders.placed")]   // a stable name, independent of the type's name
public sealed record OrderPlaced(Guid OrderId, decimal Total) : INotification;
```

Through the outbox every generated handler is its own subscription, addressed by a stable handler name (the handler type's
namespace-qualified name, or `[NotificationHandlerName("...")]`): one stored message per handler, each with its own
attempts and dead letter. Handlers registered by hand get no subscription, so a notification that goes through the outbox
does not reach them (the startup validator warns with **CQRCONF011**). Add `PartitionBy = nameof(OrderId)` to the
attribute, or implement `IPartitionedNotification`, to deliver a key's notifications to each handler in order.

[The outbox](outbox.md) covers the modes, the stores, custom serializers, ordering, the inbox and dead letters.
