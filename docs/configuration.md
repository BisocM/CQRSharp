# Configuration

CQRSharp is configured through one entry point, `AddCqrsGenerated`, and its fluent builder. This page covers the builder,
the dispatcher options (run mode and scope mode), the notification options, the background queue, hosting and lifetimes,
startup validation, and the `TimeProvider` seam.

- [AddCqrsGenerated](#addcqrsgenerated)
- [The fluent builder](#the-fluent-builder)
- [DispatcherOptions](#dispatcheroptions)
- [NotificationOptions](#notificationoptions)
- [The background queue](#the-background-queue)
- [Hosting and lifetimes](#hosting-and-lifetimes)
- [Startup validation](#startup-validation)
- [The TimeProvider seam](#the-timeprovider-seam)

## AddCqrsGenerated

The source generator writes `AddCqrsGenerated` into every assembly that references CQRSharp, as an `internal` extension
method in the `CQRSharp` namespace. It registers the CQRSharp services and the generated handler routing of the calling
assembly and of every CQRSharp assembly it references. It has two forms:

```csharp
// 1) The dispatcher, the discovered handlers, and the builder's defaults: validation and exception handling.
services.AddCqrsGenerated();

// 2) The same, configured through the fluent builder.
services.AddCqrsGenerated(builder => builder
    .UseLogging()
    .UseOutbox(o => o.UseInMemoryStore()));
```

The builder form exists where the project references `CQRSharp.Pipelines`, which the `CQRSharp` meta-package does.
There the parameterless form is the builder with nothing configured, `AddCqrsGenerated(_ => { })`: both register the
validation and exception-handling behaviors unless `UseValidation(false)` / `UseExceptionHandling(false)` turns them off.
A project that references only `CQRSharp.Core` gets the parameterless form alone, which registers no pipeline behavior,
since the behaviors live in `CQRSharp.Pipelines`.

Call `AddCqrsGenerated`, not `AddCqrs`. `AddCqrs` (hidden from IntelliSense) registers the dispatcher without the generated
routing, so the first `Send`, `Stream` or `Publish` fails. The **CQRA014** analyzer makes a direct `AddCqrs()` call a build
error and offers a fix; where the analyzers do not run, the [startup validator](#startup-validation), when it is on,
reports the missing registrations as **CQRCONF004**. How the entry points compose several assemblies is
described in [The source generator](source-generator.md#multi-assembly-applications).

## The fluent builder

Every builder verb **records** what it asks for, and the builder applies the result in one fixed sequence when it is
built. So:

- **The order of different verbs never matters.** The same registrations and the same pipeline result in any order.
- **Repeating a verb adds to it.** Configuration delegates (`ConfigureQueue`, `ConfigureDispatcher`,
  `ConfigureNotifications`, `UseRateLimiting`, `UseResilience`, `UseTimeout`, `UseUnitOfWork`'s `configure`, and the
  outbox builder's `ConfigureProcessor`) all run, in call order, so a later assignment to the same property wins. A switch, a policy, a clock or a unit-of-work factory
  (`UseValidation(bool)`, `UseExceptionHandling(bool)`, `ValidateOnStart`, `UseTimeProvider`, `UseUnitOfWork`'s
  `factory`) takes the value of the last call. Repeated `UseOutbox` or `UseIdempotency` calls configure one store builder,
  so a later call that chooses no store keeps an earlier call's choice.
- **Several `AddCqrsGenerated(builder)` calls add up.** A library's own wiring and its host's both take effect: options
  both set apply in call order, a behavior registered by one call stays registered, and an opt-out such as
  `UseValidation(false)` applies to its own call only.
- **Each verb returns the builder** for chaining.

### Behavior and store verbs

| Verb | Enables |
| --- | --- |
| `UseExceptionHandling(bool enabled = true)` | The [exception-handling behavior](pipeline-behaviors.md#exception-handling). On unless called with `false`. |
| `UseValidation(bool enabled = true)` | The [validation behavior](pipeline-behaviors.md#validation). On unless called with `false`. |
| `UseLogging()` | The logging behavior ([Observability](observability.md) lists its messages). |
| `UseRateLimiting(Action<RateLimitingOptions> configure)` | The [rate-limiting behavior](pipeline-behaviors.md#rate-limiting). |
| `UseResilience(Action<ResilienceOptions> configure)` | The resilience behavior: retries for `IRetryableRequest` ([Idempotency and resilience](idempotency-and-resilience.md)). |
| `UseTimeout(Action<TimeoutOptions> configure)` | The timeout behavior ([Idempotency and resilience](idempotency-and-resilience.md#timeouts)). |
| `UseUnitOfWork<TUnitOfWork>(Func<IServiceProvider, TUnitOfWork> factory, Action<UnitOfWorkOptions>? configure = null)` | The unit-of-work behavior for `ITransactionalCommand` / `ITransactionalQuery`, with your `IUnitOfWork` ([Unit of work](unit-of-work.md)). |
| `UseIdempotency(Action<IdempotencyStoreBuilder>? configure = null)` | The idempotency behavior for `IIdempotentRequest`, and its store ([Idempotency and resilience](idempotency-and-resilience.md)). |
| `UseOutbox(Action<OutboxStoreBuilder> configure)` | The outbox: its mode (`Enabled` unless you call `Transactional()`), its store, and processor tuning ([The outbox](outbox.md)). |

The integration packages add verbs of their own: `UseEntityFrameworkCoreUnitOfWork<TContext>(...)` from
`CQRSharp.EntityFrameworkCore` ([Integrations](integrations.md)), `UseFluentValidation()` from `CQRSharp.FluentValidation`
([FluentValidation](fluentvalidation.md)), and the store verbs `UseRedis(...)` and `UseEntityFrameworkCore<TContext>(...)`
on the outbox and idempotency store builders.

Both forms of `AddCqrsGenerated` register exception handling and validation unless they are turned off; every other
behavior is registered only by its verb. Where each behavior sits in the pipeline and which requests it acts on is described in
[Pipeline behaviors](pipeline-behaviors.md#execution-order).

### Configuration verbs

| Verb | Purpose |
| --- | --- |
| `ConfigureDispatcher(Action<DispatcherOptions> configure)` | [Run mode and scope mode](#dispatcheroptions). |
| `ConfigureNotifications(Action<NotificationOptions> configure)` | The [publish strategy](#notificationoptions). |
| `ConfigureQueue(Action<BackgroundTaskQueueOptions> configure)` | The [background queue](#the-background-queue). |
| `ValidateOnStart(bool enabled = true)` / `ValidateOnStart(CqrsValidationPolicy policy)` | The [startup validator](#startup-validation): on or off, or an explicit policy. |
| `UseTimeProvider(TimeProvider timeProvider)` / `UseTimeProvider(Func<IServiceProvider, TimeProvider> factory)` | The [authoritative clock](#the-timeprovider-seam). |
| `Services` | The underlying `IServiceCollection`, for registrations the builder does not model. |

```csharp
services.AddCqrsGenerated(builder => builder
    .UseLogging()
    .ConfigureDispatcher(o => o.ScopeMode = ExecutionScopeMode.New)
    .ConfigureNotifications(o => o.PublishStrategy = PublishStrategy.Parallel)
    .ValidateOnStart());
```

> **`Services` applies immediately.** Registrations made through `builder.Services` happen in call order, like any other
> service-collection code; only the builder's own verbs are order-independent.

The options behind these verbs are ordinary options: `services.Configure<DispatcherOptions>(...)` or a configuration
binding works as well. Every options type is validated when the host starts, and an invalid value (an undefined enum
value bound from configuration, a non-positive capacity) fails host start with `OptionsValidationException`.

## DispatcherOptions

`DispatcherOptions` governs how a *request* executes:

```csharp
public sealed class DispatcherOptions
{
    public RunMode RunMode { get; set; } = RunMode.Inline;
    public ExecutionScopeMode ScopeMode { get; set; } = ExecutionScopeMode.Current;
}
```

### RunMode

`RunMode` controls **where** a `Send` executes. It does not apply to streams: a stream runs on the flow that enumerates
it, in every run mode.

| `RunMode` | Behavior |
| --- | --- |
| `Inline` *(default)* | The handler runs on the caller's asynchronous flow. Nothing is queued. |
| `Queued` | The dispatch goes through the [background queue](#the-background-queue), which throttles execution and applies back-pressure under load. `Send` still returns a task that completes with the handler's result: queueing changes scheduling, not whether you get the result. The request runs in a DI scope of its own, whatever `ScopeMode` says. |

### ScopeMode

`ScopeMode` controls the **DI scope** a request's handler and behaviors run in. It applies to an inline `Send` and to
every stream; a queued `Send` always runs in a scope of its own. Notifications always run in the scope that publishes
them.

| `ExecutionScopeMode` | Behavior |
| --- | --- |
| `Current` *(default)* | The dispatcher's own scope. Nested sends and publishes share its scoped services (a `DbContext`, a unit of work), as in MediatR. |
| `New` | A new child scope for each sent request, and for each enumeration of a stream, kept until the enumeration ends. Use it when every request must be isolated. |

```csharp
.ConfigureDispatcher(o => o.ScopeMode = ExecutionScopeMode.New)   // inline, one scope per request
.ConfigureDispatcher(o => o.RunMode = RunMode.Queued)             // queued; each request in its own scope
```

Whatever the two modes say, a request's **context comes from its caller**: the context factory is resolved from the
scope of the dispatcher the request is sent through, and runs on the sending flow before the request is queued or given
its own scope ([Custom contexts](requests-and-handlers.md#custom-contexts)). A queued request's `CreatedAt` is therefore
the time it was sent.

## NotificationOptions

`NotificationOptions` governs how a notification reaches its handlers:

```csharp
public sealed class NotificationOptions
{
    public PublishStrategy PublishStrategy { get; set; } = PublishStrategy.Sequential;
}
```

The default is `Sequential` because a notification's handlers share the publishing DI scope. The strategies and their
failure behavior are described in [Notifications](notifications.md#publish-strategies).

```csharp
.ConfigureNotifications(o => o.PublishStrategy = PublishStrategy.ParallelWhenAllAggregate)
```

## The background queue

Under `RunMode.Queued`, sends go through a bounded background queue with a concurrency limit. The same queue runs the
work you hand to `IBackgroundTaskManager.EnqueueAsync` (`CQRSharp.Core.BackgroundTasks`). Configure it with
`ConfigureQueue`:

| `BackgroundTaskQueueOptions` | Default | Meaning |
| --- | --- | --- |
| `Capacity` | `1000` | Work items the queue holds waiting to run; running work does not count. Must be greater than zero. |
| `FullMode` | `Wait` | What an arriving item does when the queue is full: `Wait` makes the caller wait for room (its token ends the wait); `DropWrite` refuses the new item; `DropOldest` / `DropNewest` evict a queued item to make room. |
| `ConsumerCount` | `Environment.ProcessorCount` | The most work items that run at the same time. Each runs on the thread pool. Zero or negative uses the default. |
| `ShutdownTimeout` | 20 seconds | How long running work (and, with `DrainOnShutdown`, queued work) gets to finish when the host stops. |
| `DrainOnShutdown` | `true` | Whether shutdown also runs the work still queued, or cancels it at once. |
| `ConsumerStartTimeout` | 10 seconds | How long a queued `Send` waits for the queue's consumer to start before it fails. |

- **Results and failures.** Awaiting a queued `Send` returns the handler's result or throws its exception, as inline.
  A work item the queue does not run faults its caller with `BackgroundTaskRejectedException`, whose `Reason` is
  `QueueFull` (`DropWrite` and the queue was full), `Evicted` (`DropOldest` or `DropNewest` removed it) or `QueueClosed`
  (the host is stopping). CQRSharp.AspNetCore maps it to 503.
- **Cancellation.** While a send is still queued, the caller's token withdraws it: `Send` completes as cancelled at once,
  and the handler never runs. Once it has started, the handler receives the caller's token linked with the host's
  shutdown token.
- **Nested sends.** A request sent from inside any queued work item (a queued handler, or work enqueued through
  `IBackgroundTaskManager`) runs at once, in a scope of its own, instead of queueing behind the work that sent it, which
  would deadlock once every consumer slot waited for a nested request. It does not count against `ConsumerCount`.
- **Shutdown.** When the host stops, the queue refuses new work at once and then gives the running work, and the backlog,
  up to `ShutdownTimeout` to finish. With `DrainOnShutdown = false` the backlog is cancelled as soon as the queue closes,
  and only the running work gets the budget. What the budget does not cover is cancelled: a
  running handler sees its token fire, and a caller awaiting an item that never started gets an
  `OperationCanceledException`. The host's own `HostOptions.ShutdownTimeout` (30 seconds by default) also cancels the
  remaining work when it runs out, and it is shared by every hosted service, so keep `ShutdownTimeout` clearly below it.

The queue's metrics are described in [Observability](observability.md).

## Hosting and lifetimes

CQRSharp is built for the .NET **Generic Host**:

- **Queued dispatch, the outbox and startup validation need a started host.** The queue's consumer, the outbox processor
  and the startup validator are hosted services, which run only after `host.StartAsync()` or `RunAsync()`. Under the
  defaults (`RunMode.Inline`, outbox off, validation off) nothing depends on them. Without a started host a queued `Send`
  waits `ConsumerStartTimeout` for the consumer and then throws `InvalidOperationException` instead of hanging, the
  outbox stores messages that nothing delivers, and nothing is validated.
- **Resolve `ICqrsDispatcher` from a scope, not the root provider.** The dispatcher and its pipeline are *scoped*, so a
  handler's scoped dependencies (an EF `DbContext`, a unit of work) get one instance per DI scope: per web request, or
  per scope you create, shared by every dispatch in that scope under the default `ScopeMode.Current`. Hosted services,
  controllers and minimal-API handlers already run in a scope; elsewhere, create one:

  ```csharp
  using var scope = provider.CreateScope();
  var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
  ```

  Resolved from the **root** provider, the dispatcher runs handlers and their scoped dependencies as process-lifetime
  singletons. Building the provider with `ValidateScopes` (the default in the Development environment) turns that mistake
  into an error.
- **Handlers are transient unless you register them yourself.** The generated registrations use `TryAdd` for each
  handler's concrete type, so a registration you make first chooses the lifetime (`services.AddSingleton<PriceCache>()`
  before `AddCqrsGenerated()`), and dispatch resolves the handler through it.

## Startup validation

The startup validator inspects the configuration and every request's binding once, before any hosted service starts, so
a seeder or a web server never runs against a configuration it would reject. It is off by default
(`CqrsStartupValidationOptions.Policy` is `Off`); turn it on with `ValidateOnStart`:

```csharp
.ValidateOnStart()                                          // ThrowOnError: abort host start on an error
.ValidateOnStart(CqrsValidationPolicy.ThrowOnWarning)       // abort on errors and warnings
.ValidateOnStart(CqrsValidationPolicy.WarnOnly)             // log everything, never abort
.ValidateOnStart(false)                                     // Off
```

| `CqrsValidationPolicy` | Effect |
| --- | --- |
| `Off` *(default)* | The validator does not run. `ValidateOnStart(false)` selects it. |
| `WarnOnly` | Every issue is logged; host start always proceeds. `ValidateOnStart(CqrsValidationPolicy.WarnOnly)`. |
| `ThrowOnError` | Every issue is logged; host start is aborted when an error is present. `ValidateOnStart()` selects it. |
| `ThrowOnWarning` | Every issue is logged; host start is aborted when an error or a warning is present. `ValidateOnStart(CqrsValidationPolicy.ThrowOnWarning)`. |

A builder that never calls `ValidateOnStart` leaves the policy as it is, so it can also come from another
`AddCqrsGenerated` call or from `services.Configure<CqrsStartupValidationOptions>(...)`.

The validator reports issues with stable `CQRCONF` codes: outbox wiring (a missing store or serializer, a handled
notification that bypasses the outbox, a notification or handler name used twice, handlers the outbox cannot reach or
the validator cannot construct), a
transactional outbox without a unit of work, missing generated registrations, and idempotency or retry markers whose
behavior was never registered. Problems with a request's own binding are reported with `CQRDIAG` codes. [Diagnostics and validation](diagnostics.md)
lists every code and its severity.

## The TimeProvider seam

Every time-dependent component reads the clock through the registered `TimeProvider`: timeouts, retry back-off,
rate-limiter refill, leases, expiry and timestamps. That makes them deterministic under a fake clock. `AddCqrsGenerated`
registers `TimeProvider.System` with `TryAdd`, so a `TimeProvider` your host registered is used as it is. To force a
specific provider, use `UseTimeProvider`, which replaces any earlier registration (the last call wins):

```csharp
.UseTimeProvider(fakeTimeProvider)                          // an instance
.UseTimeProvider(sp => sp.GetRequiredService<MyClock>())    // a factory that resolves a distinct clock type
```

> **The factory must return a concrete provider:** resolve a *distinct* clock type, as above, or return
> `TimeProvider.System`. It must not resolve `TimeProvider` itself (`sp.GetService<TimeProvider>()`): the factory *is* the
> `TimeProvider` registration, so that recurses. CQRSharp detects it and throws `InvalidOperationException` instead of
> hanging. A factory that returns `null` throws too.

In tests, register a `FakeTimeProvider` (`Microsoft.Extensions.Time.Testing`) and advance it; see [Testing](testing.md).
