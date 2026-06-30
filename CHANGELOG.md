# Changelog

All notable changes to CQRSharp are documented here. This project adheres to [Semantic Versioning](https://semver.org/).

## [4.1.0]

A focused ease-of-use release: make the first dispatch succeed, make failures self-explanatory, and make the
discoverable path the correct path.

### Added

- **`dotnet new cqrsharp` template** and a **`CQRSharp.Sample.Minimal`** project — a complete, ~30-line app that
  registers CQRSharp, sends one request, and prints the result. `docs/getting-started.md` now opens with the same
  runnable program instead of fragments assembled across pages.
- **Five new analyzers and one new generator diagnostic** that turn silent runtime failures into build-time warnings:
  - **`CQRA011`** (Warning) — a request declares a custom context but no `IRequestContextFactory<TContext>` is
    discoverable (in this compilation or, via a new generator-emitted assembly marker, a referenced one), so dispatch
    would throw. 
  - **`CQRA012`** (Warning) — an `IRequestValidator<T>` is declared where CQRSharp is registered, but no pipeline pack
    is enabled, so the validator never runs and input reaches the handler unvalidated.
  - **`CQRA013`** (Info) — resilience/idempotency is configured but a request does not implement
    `IRetryableRequest`/`IIdempotentRequest`, so it is silently passed through (often intentional).
  - **`CQRA014`** (Error, with code fix) — `AddCqrs(...)` is called directly in user code; it leaves dispatch
    unrouted. The fix rewrites it to `AddCqrsGenerated(...)`.
  - **`CQRA017`** (Info) — a type implementing both `IPreHandlerAttribute` and `IPostHandlerAttribute` can be a single
    `ICommandInterceptor`.
  - **`CQRGEN010`** (Warning) — a handler binding is skipped because a bound request/result/context/notification type
    argument is less accessible than `internal` (the handler itself is accessible), which previously surfaced only as a
    runtime "no handler".
- **`CQRCONF007`** — startup warning when the outbox is `Transactional` but no `IUnitOfWork` is registered at all, so
  every notification silently dispatches in-process.
- **`CQRCONF008`** — startup error when `RunMode.Queued` is combined with a stream request (streaming has no queued
  path and throws at the first `Stream(...)`).
- **`ModelBuilder.ApplyCqrsOutbox()` / `ApplyCqrsIdempotency()`** (CQRSharp.EntityFrameworkCore) — apply the outbox /
  idempotency entity mappings in one call from `OnModelCreating`.
- **`ICqrsBuilder.ValidateOnStart(CqrsValidationPolicy)`** — choose any policy (`WarnOnly`, `ThrowOnWarning`, …), not
  just the on/off the `bool` overload exposed.
- **Named pipeline continuations** — `RequestHandlerDelegate<TResult>` / `StreamHandlerDelegate<TItem>` replace the bare
  `Func<…>` `next` parameter, so a behavior can `await next()` (the token is defaulted) and the type self-documents.
- The meta-package's global usings now include `CQRSharp.Core.Pipelines` and `CQRSharp.Core.Notifications.Pipelines`, so
  custom pipeline/notification behaviors resolve without manual imports.
- **Async context hydration.** Request context factories can now create the context asynchronously: derive from the new
  `AsyncRequestContextFactory<TContext>` and override `CreateContextAsync` to load request-scoped data (for example, the
  current user aggregate) from async sources at a single awaited point before the pipeline runs — instead of blocking in
  the factory or scattering lazy loads through the handler. The dispatcher awaits creation across the query, command, and
  stream paths. Synchronous factories are unchanged: the new `IInternalRequestContextFactory.CreateContextAsync` defaults
  to wrapping the existing synchronous `CreateContext`.
- **Outcome-aware post-handlers.** A post-handler can now receive the request's `RequestOutcome` — the value the handler
  returned *or* the exception it threw — not just the request. Derive from `OutcomeAwarePostHandlerAttribute` (or
  implement `IPostHandlerOutcomeAware`) to classify on what actually happened: this is what lets a cross-cutting concern
  such as auditing distinguish a login that *returned* a "bad credentials" verdict (a successful dispatch carrying a
  denial) from one that succeeded, without lying via `FromError`/throw. Outcome-aware post-handlers also run on the
  exception path (isolated, so they cannot mask the original error); plain `IPostHandlerAttribute` post-handlers keep
  their existing success-only behavior.

### Changed

- **`AddCqrs` is now hidden from IntelliSense (`[EditorBrowsable(Never)]`)** and its XML doc points at
  `AddCqrsGenerated`. It remains callable (the builder and generated bootstrap use it internally), but `CQRA014` flags
  any direct use in your code.
- **`UseValidation()` / `UseExceptionHandling()` are honest.** They now accept a `bool` (`UseValidation(false)` opts
  out), and the docs state the real default: validation and exception handling are on whenever the pipeline pack is
  active (i.e. whenever any pack verb is used), not "off by default".
- Exception messages that pointed at the wrong registration verb (`AddCqrs()`, the internal `AddGenerated()`) now name
  `AddCqrsGenerated(...)` and add the resolve-from-a-scope hint. A guard test keeps them from regressing.
- `IPipelineBehaviour.cs` was renamed to `IPipelineBehavior.cs` (the only British-spelled file; the type was already
  `IPipelineBehavior`).

### Fixed

- Documentation corrections: the false "custom context falls back to `RequestContextBase`" claim, the "all built-ins
  off by default" claim, undocumented `CQRDIAG001`–`004`, the missing `ICommandInterceptor` documentation, and the
  open-generic-notification (`INotificationPipelineBehavior<TN>`) audit pattern.

## [4.0.3]

### Fixed

- **Multi-assembly setup is now zero-config.** The generated `AddCqrsGenerated`/`AddGenerated` entry points are emitted
  **`internal`**, so two assemblies that both emit them never collide — a referenced assembly's copy is invisible to the
  referencing one. This removes the "composition root" concept entirely: there is nothing to designate, and a **test
  project that references the host it tests just works** (previously the test SDK made it executable, so it self-elected
  as a second composition root and collided with the host — `CS0121`). Put the package on your handler projects and your
  host, call `AddCqrsGenerated()`, done.
- **`RunMode.Queued` no longer hangs without a running host.** Queued dispatch is drained by a background service that
  only runs once the Generic Host starts. If the host never starts (a plain console, a DI-only test), a queued
  `Send(...)` now waits up to `BackgroundTaskQueueOptions.ConsumerStartTimeout` (default 10 s) and throws a clear error,
  instead of awaiting a task nothing would ever complete.

### Added

- **`CQRGEN009`** — a warning when an open-generic handler (`Handler<T>`) is declared. CQRSharp registers only closed,
  non-generic handlers, so an open-generic one was previously silently unwired and failed only as a runtime "no handler".
- **`CQRA010`** — a warning when a project declares CQRSharp handlers but the source generator is not running in it (so
  no module is emitted and its handlers go unregistered). To make this fire even in a project that is *missing* the
  generator, the **CQRSharp analyzers now ship with `CQRSharp.Abstractions`** (which every handler project references), in
  addition to the meta-package — a packaging change with no API impact.
- **`BackgroundTaskQueueOptions.ConsumerStartTimeout`** — bounds the queued-dispatch wait described above.

### Changed

- **The `CQRSharpCompositionRoot` MSBuild property is no longer needed and is ignored.** Introduced in 4.0.2, it is
  obsoleted by the internal entry points above. Setting it has no effect (and no longer needs removing).

## [4.0.2]

### Fixed

- **Multi-assembly applications.** CQRSharp can now span several assemblies in one reference graph (e.g. handlers split
  across `Application` / `Infrastructure` / per-feature projects). Previously the generator emitted fixed-name DI types
  into every assembly it ran in, so two in the same graph collided (`CS0121` on the generated `AddGenerated`), and each
  assembly's dispatcher only knew its own requests. Now each assembly emits its own uniquely-namespaced **module**, and
  the **composition root** emits the single `AddCqrsGenerated()` / `AddGenerated()` that wires every module in the graph.

### Added

- **`ICqrsModule` + `AddCqrsModuleComposition`** (CQRSharp.Core) and the **`[CqrsGeneratedModule]`** assembly marker
  (CQRSharp.Abstractions): each assembly with handlers contributes a module; the composition root merges them into one
  set of registries and routing dispatchers. A referenced assembly's `internal` handlers are registered too, because
  that assembly's own module registers them — the root never needs to name them.
- **`CQRSharpCompositionRoot`** MSBuild property. The composition root is your executable by default; set
  `<CQRSharpCompositionRoot>true</CQRSharpCompositionRoot>` when the entry point is a library (a plugin host, a test host).

## [4.0.1]

### Added

- **Value-returning commands.** `ICommand<TResult>` — with `ResultCommandBase<TResult>` /
  `ResultCommandBase<TResult, TContext>` and `IResultCommandHandler<TCommand, TResult>` — models a command that
  mutates state and returns a value no query could reproduce: a secret minted at the instant of the operation and
  never persisted in readable form (a one-time API key, a generated token shown once). The handler returns a new
  `CommandResult<TResult>` (the outcome plus the value on success; its `ToString()` never prints the value). The
  analyzer **CQRA009** raises an informational reminder on each `ICommand<TResult>` declaration so the choice stays
  deliberate. A value-returning command dispatches through the query path, so it publishes the query lifecycle
  notifications rather than the command ones.

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
