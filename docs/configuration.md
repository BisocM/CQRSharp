# Configuration

CQRSharp is configured through one entry point — `AddCqrsGenerated` — and an order-insensitive fluent
builder. This page covers the builder, the dispatcher options (run mode and scope mode), notification
options, the `TimeProvider` seam, the background queue, and startup validation.

- [AddCqrsGenerated](#addcqrsgenerated)
- [The fluent builder](#the-fluent-builder)
- [DispatcherOptions](#dispatcheroptions)
- [NotificationOptions](#notificationoptions)
- [The background queue](#the-background-queue)
- [Startup validation](#startup-validation)
- [The TimeProvider seam](#the-timeprovider-seam)

## AddCqrsGenerated

`AddCqrsGenerated` is emitted into your assembly by the source generator. It has two forms:

```csharp
// 1) Register the discovered handlers, dispatcher, and registries.
services.AddCqrsGenerated();

// 2) Same, plus configure behaviors and stores through the fluent builder.
services.AddCqrsGenerated(builder => builder
    .UseValidation()
    .UseOutbox(o => o.Transactional().UseInMemoryStore()));
```

Always use `AddCqrsGenerated` (not `AddCqrs`). It applies the source-generated registrations that the
dispatcher and diagnostics require; if they are missing, the startup validator raises **CQRCONF004**.

## The fluent builder

Every builder verb only **records intent**. The accumulated configuration is applied in one fixed,
canonical sequence when the builder builds, so **the order you call the verbs in never matters** — the
same registrations and the same resolved pipeline result regardless of order. Each verb returns the
builder for chaining.

### Behavior verbs

| Verb | Enables |
| --- | --- |
| `UseLogging()` | Per-request logging (start, completion with elapsed time, failure). Runs outermost. |
| `UseValidation()` | Runs every registered `IRequestValidator<TRequest>` before the handler. |
| `UseExceptionHandling()` | Request-level exception hooks (`IRequestExceptionHandler` / `IRequestExceptionAction`). |
| `UseRateLimiting(Action<RateLimiterOptions>)` | Token-bucket rate limiting keyed on the request context. |
| `UseResilience(Action<ResilienceOptions>)` | Automatic retries for `IRetryableRequest`. |
| `UseTimeout(Action<TimeoutOptions>)` | A per-request timeout. |
| `UseUnitOfWork<TUoW>(factory, configure?)` | Wraps `ITransactionalCommand` / `ITransactionalQuery` in a transaction. |
| `UseOutbox(Action<OutboxStoreBuilder>)` | The transactional outbox (mode + store + processor). |
| `UseIdempotency(Action<IdempotencyStoreBuilder>?)` | At-most-once processing for `IIdempotentRequest`. |
| `UsePipelinePack(Action<CqrsPipelinePackOptions>?)` | Configure several behaviors at once via the pack accumulator. |

The behavior verbs are the **only** public way to enable pipeline behaviors. See
[Pipeline behaviors](pipeline-behaviors.md) for what each does, and [The outbox](outbox.md) /
[Idempotency & resilience](idempotency-and-resilience.md) / [Unit of work](unit-of-work.md) for the
reliability ones.

### Configuration verbs

| Verb | Purpose |
| --- | --- |
| `ConfigureDispatcher(Action<DispatcherOptions>)` | Run mode and scope mode. |
| `ConfigureNotifications(Action<NotificationOptions>)` | The notification publish strategy. |
| `ConfigureQueue(Action<BackgroundTaskQueueOptions>)` | The background task queue (capacity, concurrency). |
| `ValidateOnStart(bool = true)` | Enable/disable the fail-fast startup validator. |
| `UseTimeProvider(TimeProvider)` / `UseTimeProvider(Func<IServiceProvider, TimeProvider>)` | Set the authoritative clock. |
| `Services` | The underlying `IServiceCollection` — an escape hatch for registrations the builder doesn't model. |

```csharp
services.AddCqrsGenerated(builder => builder
    .UseLogging()
    .UseValidation()
    .ConfigureDispatcher(o => o.ScopeMode = ExecutionScopeMode.New)
    .ConfigureNotifications(o => o.PublishStrategy = PublishStrategy.Parallel)
    .ValidateOnStart());
```

> **`Services` applies immediately.** Unlike the builder's own verbs, registrations you make through
> `builder.Services` take effect in call order, exactly like any plain service-collection usage.

## DispatcherOptions

`DispatcherOptions` governs how a *request* executes. It carries two independent axes:

```csharp
public sealed class DispatcherOptions
{
    public RunMode            RunMode   { get; set; } = RunMode.Inline;
    public ExecutionScopeMode ScopeMode { get; set; } = ExecutionScopeMode.Current;
}
```

### RunMode

Controls **where** a dispatch executes. Both modes are awaitable and both return the handler's result —
the difference is scheduling, not fire-and-forget.

| `RunMode` | Behavior |
| --- | --- |
| `Inline` *(default)* | Run the handler inline on the caller's asynchronous flow. Nothing is queued. |
| `Queued` | Funnel the dispatch through the shared background task queue. The call still returns an awaitable task that completes with the handler's result, but execution is centrally throttled and scheduled for back-pressure under load. Not supported for streaming requests. |

### ScopeMode

Controls the **DI scope** a request executes in:

| `ExecutionScopeMode` | Behavior |
| --- | --- |
| `Current` *(default)* | Execute in the current DI scope. Nested sends/publishes share scoped services (`DbContext`, unit of work, …) — MediatR-style semantics. |
| `New` | Create a fresh child scope per dispatch. Use when you want each request fully isolated. |

```csharp
.ConfigureDispatcher(o =>
{
    o.RunMode   = RunMode.Queued;
    o.ScopeMode = ExecutionScopeMode.New;
})
```

## NotificationOptions

`NotificationOptions` governs how a *notification* fans out to its handlers — a separate concern from
request execution, with its own concurrency semantics:

```csharp
public sealed class NotificationOptions
{
    public PublishStrategy PublishStrategy { get; set; } = PublishStrategy.Sequential;
}
```

The default is `Sequential` because a notification's handlers share the dispatching DI scope. See
[Notifications](notifications.md#publish-strategies) for the full strategy table.

```csharp
.ConfigureNotifications(o => o.PublishStrategy = PublishStrategy.ParallelWhenAllAggregate)
```

## The background queue

When `RunMode.Queued` is configured, dispatches are funneled through a bounded background task queue
with a fixed concurrency limit, providing centralized throttling and back-pressure. Configure it with
`ConfigureQueue(Action<BackgroundTaskQueueOptions>)` (capacity and max concurrency). When the queue is
full it rejects new work and publishes a `TaskRejectedNotification`; accepted work publishes a
`TaskEnqueuedNotification`. The result of a queued dispatch flows back to the caller through a
completion source — awaiting `Send(...)` works identically to inline mode.

Queued dispatch requires a **running Generic Host**: the queue is drained by a `BackgroundService` that runs only after
the host starts. If you build the provider without starting the host (a plain console, a DI-only test), a queued
`Send(...)` waits up to `BackgroundTaskQueueOptions.ConsumerStartTimeout` (default 10 s) for the consumer and then throws
a clear error instead of hanging forever.

## Hosting & lifetimes

CQRSharp is built for the .NET **Generic Host**. Two consequences are worth knowing for non-standard setups:

- **Queued dispatch and the outbox need a started host.** Both are driven by background `IHostedService`s (the queue
  consumer, the outbox processor), which run only after `host.StartAsync()`/`RunAsync()`. The startup validator is also
  a hosted service. So if you build a provider and never start the host, none of them run. Under the defaults
  (`RunMode.Inline`, outbox disabled) this is irrelevant; turn either on and you need a running host. Queued dispatch
  fails fast in that case (above); the outbox would silently not deliver.
- **Resolve `ICqrsDispatcher` from a scope, not the root provider.** The dispatcher and its pipeline are *scoped*, so a
  handler's scoped dependencies (an EF `DbContext`, a unit of work) get a fresh instance per dispatch. Hosted services,
  controllers, and minimal-API handlers already run in a scope. In console or background-job glue, create one:

  ```csharp
  using var scope = provider.CreateScope();
  var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
  ```

  Resolving the façade straight from the **root** provider silently runs handlers (and their scoped dependencies) as
  process-lifetime singletons — a captive-dependency hazard. Enabling `ValidateScopes` on `BuildServiceProvider` turns
  that mistake into an explicit error.

## Startup validation

CQRSharp ships a hosted **startup validator** that inspects the wired-up configuration and per-request
bindings once at host start, turning previously silent fallbacks into loud, early failures. Toggle it
with `ValidateOnStart`:

```csharp
.ValidateOnStart()        // enabled: abort host start on a configuration error
.ValidateOnStart(false)   // disabled
```

The behavior is governed by `CqrsValidationPolicy`:

| Policy | Effect |
| --- | --- |
| `Off` | The validator does not run. (`ValidateOnStart(false)` maps here.) |
| `WarnOnly` | Log all issues, but never abort host start — even on errors. |
| `ThrowOnError` | Log all issues; abort host start if any **error** is present. (`ValidateOnStart()` maps here.) |
| `ThrowOnWarning` | Abort host start if any error **or warning** is present. |

The validator reports stable `CQRCONF` codes — an enabled outbox with no store, a transactional outbox
that can never see a transaction, an in-process-only notification, a missing generated registry, and an
idempotency/retry marker whose behavior was never registered. See
[Diagnostics & validation](diagnostics.md#startup-validation-cqrconf) for every code.

## The TimeProvider seam

Every time-dependent component in the runtime reads the clock through an injected `TimeProvider`
(durations use `GetElapsedTime`). This makes timeouts, retry back-off, rate-limiter refill, and outbox
timestamps deterministically testable. Override the authoritative provider with `UseTimeProvider`
(last-writer-wins, regardless of where it appears in the chain):

```csharp
.UseTimeProvider(myFakeTimeProvider)                       // an instance
.UseTimeProvider(sp => sp.GetRequiredService<MyClock>())   // a factory
```

In tests, register a `FakeTimeProvider` and advance it to exercise time-dependent behavior without real
delays. See [Testing](testing.md).
