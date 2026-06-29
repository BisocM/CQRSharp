# Changelog

All notable changes to CQRSharp are documented here. This project adheres to [Semantic Versioning](https://semver.org/).

## [4.0.0]

### Breaking changes

- **`RunMode` members renamed.** `RunMode.Sync` → `RunMode.Inline` and `RunMode.Async` → `RunMode.Queued`. The names now
  describe *where* a dispatch runs (inline on the caller's flow vs. funneled through the background queue); both modes
  still return the handler's result to the caller — `Queued` is not fire-and-forget.
- **`ITransactionalRequest` renamed to `ITransactionalCommand`** for symmetry with `ITransactionalQuery` (it has always
  been command-only, `: ICommand`).
- **Notification publish strategy moved.** `PublishStrategy` moved off `DispatcherOptions` onto a new `NotificationOptions`
  (set it with the `ConfigureNotifications(...)` builder verb). Its default is now `Sequential` (was
  `ParallelWhenAllAggregate`): a notification's handlers share the dispatching DI scope, so the safe default runs them
  one at a time. Opt into a parallel strategy only when the handlers are independent.
- **One lane for pipeline behaviors.** The direct `AddExceptionHandling`/`AddIdempotency`/`AddLoggingBehavior`/
  `AddResilienceBehavior`/`AddTimeoutBehavior`/`AddValidationBehavior`/`AddRateLimiting`/`AddUnitOfWorkBehavior`
  extensions and `AddCqrsPipelinePack` are now internal. Enable behaviors through the fluent builder verbs
  (`UseValidation()`, `UseResilience(...)`, `UseIdempotency(...)`, …) on `AddCqrsGenerated(b => ...)`.
- **Outbox is off by default.** `OutboxOptions.Mode` defaults to `Disabled`; enable it with `UseOutbox(...)`. The
  `UseInMemoryOutbox()`/`UseInMemoryIdempotency()` builder verbs and the ambiguous `AddCqrs(Action<ICqrsBuilder>)`
  overload were removed in favor of the cohesive `UseOutbox(o => o.UseInMemoryStore())` / `UseIdempotency(i => ...)`.
- **Rate-limiting validation** now fails through the options system (`Validate` + `ValidateOnStart`, surfacing an
  `OptionsValidationException`) instead of throwing inside the `Configure` delegate.

### Added

- **`CQRCONF005` / `CQRCONF006` startup checks** — the configuration validator now reports an `IIdempotentRequest` or
  `IRetryableRequest` marker whose idempotency/resilience behavior was never registered (an otherwise silent no-op).
- **`CQRA008` analyzer + code fix** — suggests the open-generic `[PipelineExemption(typeof(Behavior<,>))]` shorthand over
  the verbose closed-generic form (which the runtime already accepts).
- **Streaming on `netstandard2.0`** — `IStreamRequest`/`IStreamRequestHandler`/`StreamRequestBase` now compile on the
  `netstandard2.0` target.
- **Richer AOT outbox serialization** — the generated, reflection-free notification serializer now handles nested
  objects and collections, not just scalar properties.
- **XML documentation on the generated public API.**

### Changed

- The CQRSharp source generators were reworked onto an equatable-records incremental pipeline (improved incrementality
  and no latent symbol retention) while emitting byte-identical generated output.

## [3.0.0]

### Breaking changes

- **Namespace flatten.** The redundant `Data` segment was removed from the `CQRSharp.Abstractions` namespaces
  (e.g. `CQRSharp.Abstractions.Data.Interfaces.Markers.Command` → `CQRSharp.Abstractions.Interfaces.Markers.Command`).
  Update `using` directives accordingly.
- **Pipeline behavior namespaces.** `CQRSharp.Pipelines.Types.*` → `CQRSharp.Pipelines.Behaviors.*`.
- **Outbox contract.** `IOutboxStore` now uses claim-based delivery: `GetPendingAsync` atomically claims due messages
  (`Pending` → `InProgress`), a new `IncrementAttemptAsync` records failed attempts durably, and `OutboxMessage`
  gained `AttemptCount`/`NextRetryAt` and renamed `Error` to `LastError`. Custom stores must implement the new member.
- **Opt-in retries.** Automatic retries by the resilience behavior now require requests to implement the new
  `IRetryableRequest` marker. Non-idempotent requests are no longer retried by default. Caller cancellation and
  timeouts are never retried.
- Removed the unused `Microsoft.Extensions.DependencyInjection.Abstractions` dependency from `CQRSharp.Abstractions`
  (the `netstandard2.0` build now references `Microsoft.Bcl.AsyncInterfaces` directly).

### Added

- `IRetryableRequest` opt-in marker for automatic retries.
- `Command`/`Query`/`Stream` `Failed` lifecycle notifications, published when a handler faults.
- First-class dispatcher configuration via `AddCqrs(..., configureDispatcher)` / `AddCqrsGenerated(..., configureDispatcher)`.
- Public `CqrsPipelinePriorities` constants for ordering custom behaviors relative to the built-ins.
- Start-up options validation (`ValidateOnStart`) for the background queue, outbox, timeout, and resilience options.
- Durable outbox retry/back-off with dead-lettering driven by a persisted attempt count.

### Fixed

- Release/NuGet publish workflow now triggers on the `Release` branch (previously a non-existent `main`).
- Background queue no longer drops a dequeued task on shutdown (which left awaiting callers hung forever).
- Notification pump no longer leaks a semaphore permit on the no-dispatcher path (which could deadlock the pump).
- Resilience behavior runs outside the unit of work, so each retry executes against a fresh transaction.
- Notification fan-out surfaces every handler failure (not just the first) and isolates synchronously-throwing handlers.
- Token-bucket rate limiter refills continuously rather than only in whole-second chunks, with torn-read-safe access.
- Unit-of-work rollback failures no longer mask the original error.
