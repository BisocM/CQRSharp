# Changelog

All notable changes to CQRSharp are documented here. This project adheres to [Semantic Versioning](https://semver.org/).

## [5.0.0]

The release that makes CQRSharp complete. The authoring surface is **two namespaces** instead of a dozen; a request
dispatch costs about what a MediatR dispatch does (it was ~6x slower); the outbox and the idempotency store were
redesigned around **owned claims**, so a slow processor can no longer double-deliver and a duplicate request gets the
**original result replayed** instead of an error; and an audit of the runtime, the behaviors, both persistence
integrations and the generator closed several ways a notification could be silently lost, a release-blocking DI bug in
the EF Core idempotency store, and legal user code the generator turned into a broken build. New packages:
**`CQRSharp.AspNetCore`**, **`CQRSharp.FluentValidation`**, **`CQRSharp.Testing`** and **`CQRSharp.Templates`**.

### Migrating from 4.x

1. **Usings.** Replace every `using CQRSharp.Abstractions.*;` / `using CQRSharp.Core.*;` that names the authoring surface
   with `using CQRSharp;`, and add `using CQRSharp.Pipelines;` where you configure the builder, write a behavior, or
   implement a store / unit of work. With the `CQRSharp` meta-package and `ImplicitUsings` you need neither. Runtime
   internals (`CQRSharp.Core.Pipelines`, `.Modules`, `.Diagnostics`, `.Background.*`, …) did not move.
2. **Redis outbox.** The default `KeyPrefix` changed; set `o.KeyPrefix = "cqrsharp:outbox:"` to keep draining messages
   a 4.x deployment stored (see below).
3. **Failed results.** A handler that *returns* `CommandResult.FromError(...)` now rolls back its unit of work. If you
   rely on persisting state before returning a failure, set `UnitOfWorkOptions.RollbackOnFailedResult = false`.
4. **EF Core schema.** The idempotency table gained `Completed` and `Result` columns (result replay) and the outbox
   table uses its `RowVersion` as the claim token. Add a migration after upgrading
   (`dotnet ef migrations add CqrSharp5`).
5. **Custom stores.** `IOutboxStore` and `IIdempotencyStore` changed shape (claims - see below). Derive a test class
   from the contract suites in `CQRSharp.Testing` to check a custom store against the new contract.
6. **Duplicates.** A duplicate of a *completed* idempotent request now returns the stored result rather than throwing
   `DuplicateRequestException`; the exception remains for a duplicate that arrives while the original is still running
   (`IsInProgress`), and for result types that cannot be replayed.
7. Hand-written `ICqrsModule` / `IHandlerRegistry` implementations (rare - the generator writes these) must move to the
   typed invoker shape: `HandlerInvokers` is `IReadOnlyDictionary<Type, Delegate>`, and `HandlerInvokerDelegate` is gone.

### Changed

- **Namespaces — breaking.** The consumer-facing surface is flattened to **`CQRSharp`** (requests and base classes,
  handler interfaces, `CommandResult`, notifications and the lifecycle notification types, request context and its
  factories, `RequestMetadata`, interceptor contracts, validators, exception hooks, the idempotency / retry /
  transactional markers, `ICqrsDispatcher`, `AddCqrs` / `AddCqrsGenerated` and their options) and
  **`CQRSharp.Pipelines`** (`ICqrsBuilder` and the behavior option types, `IPipelineBehavior` /
  `IStreamPipelineBehavior` / `INotificationPipelineBehavior` and their delegates, `IUnitOfWork`, `IIdempotencyStore`,
  `IOutbox` / `IOutboxStore` / `OutboxMessage`, `INotificationSerializer`, the store builders,
  `RateLimitExceededException`). Only namespaces moved: assemblies, package ids and folders are unchanged. The
  `[Obsolete]` `IRateLimitedContext` alias under `CQRSharp.Pipelines.Behaviors.RateLimiting.Context` is removed.
- **Dispatch performance.** Measured with `benchmarks/CQRSharp.Benchmarks` (trivial handlers, .NET 8): a request went
  from ~465 ns / 768 B to ~80 ns / 152 B, a notification publish from ~92 ns to ~60 ns, and a dispatch from a fresh DI
  scope from ~483 ns to ~215 ns. None of the old cost was reflection; it was work done unconditionally per dispatch.
  - A **request plan** per request type per provider caches the registry lookups and sorted interceptors, and records
    whether the provider has *any* pipeline behavior or lifecycle-notification subscriber registered for the request.
    Stages with nothing registered are skipped rather than resolved-and-found-empty. **Lifecycle notifications are
    therefore only published when something subscribes to them** (a handler or notification behavior for that
    notification type); a replaced `INotificationDispatcher`, or a container that cannot answer registration queries,
    keeps the always-publish behavior.
  - When nothing brackets the handler, `Send` returns the handler's own task — no state machines, no closures. A
    synchronous throw still surfaces through the task and the null-result contract is still enforced.
  - The generator emits **typed handler invokers** (no boxing) and **exact-type route tables** in place of a
    type-pattern switch, which was linear in the number of request types; routes are merged into one frozen table per
    provider. A request is matched by its exact runtime type.
  - Creating a DI scope no longer rebuilds a dictionary of every request type; `ICqrsDispatcher` resolves its
    sub-dispatchers lazily, and `IPipelineExecutor` / `IRequestDispatcher` / `IStreamRequestDispatcher` are registered
    transient (the scoped `ICqrsDispatcher` keeps the instance it resolves).
- **A command that returns a failed `CommandResult` is a failure — breaking.** The unit of work rolls back (or, for an
  implicit one, does not save) and drops the notifications the command published; the request-level outbox flush
  abandons them; and the idempotency claim is released, so the caller's retry is no longer rejected as a duplicate for
  the whole retention window. `UnitOfWorkOptions.RollbackOnFailedResult = false` restores commit-on-failed-result.

- **The outbox can no longer silently drop a notification — breaking.** In `OutboxMode.Enabled` every durable
  (`[NotificationName]`) notification was buffered in the scoped `IOutbox`, but only a *transactional* unit-of-work ever
  drained that buffer. A notification published by a plain command, by a request with no unit-of-work behavior, or
  straight from a controller / hosted service was discarded when the scope ended — no store write, no handler call, no
  error. The request now owns the buffer: whatever a unit of work has not already persisted atomically is **stored when
  the request succeeds**, and a publish from **outside any request is written straight to the `IOutboxStore`**. The same
  applies in `Transactional` mode when the transaction is one your own code started.
- **A failed request's notifications are discarded.** A handler (or pre-handler) that throws no longer leaves what it
  published in the scoped outbox, where a retry attempt would persist it twice (`UseResilience` + `UseUnitOfWork`) or the
  next command in the same scope would commit it for work that never happened. A rolled-back unit of work drops them too.
- **Request lifecycle contract — breaking for subscribers.** Once `*Initiated` is published a request now *always*
  reaches a terminal notification: a throwing **pre-handler** produces `CommandFailed` / `QueryFailed` /
  `StreamFailed` and runs the outcome-aware post-handlers (it used to produce neither). A faulting `*Failed` subscriber
  can no longer replace the handler's exception. **Streams** now honour the 4.2.0 post-handler contract — a faulted
  stream reaches `OnAfterHandle` with `RequestOutcome.Threw` — and mark their tracing activity as failed.
- **Retry semantics.** `UseResilience` treated *every* `OperationCanceledException` as caller cancellation, so the classic
  transient fault — an `HttpClient` timeout surfacing as `TaskCanceledException` — was never retried. Only a cancellation
  of the caller's own token is terminal now. A `DuplicateRequestException` is terminal too (it used to be retried through
  the whole back-off schedule). `UseTimeout` no longer reports a caller cancellation that races the deadline as a
  `TimeoutException`.
- **Outbox delivery is claim-based — breaking for custom stores.** `GetPendingAsync` leases each message and hands back
  an `OutboxClaim` (`OutboxMessage.Claim`); `MarkAsProcessedAsync`, `IncrementAttemptAsync` and `MarkAsFailedAsync` take
  that claim and **do nothing if it has been lost**, so a processor that outlived its lease can no longer overwrite the
  outcome recorded by the instance that took the message over. The processor **renews** a claim once half its lease is
  gone (`RenewAsync`), **releases** what it still holds on shutdown (`ReleaseAsync`) so another instance picks the work
  up immediately instead of after the visibility timeout, and records a final failed attempt with a single
  `MarkAsFailedAsync` call. All three stores implement it: a row version (EF Core), a claim field checked inside the
  Lua scripts (Redis), a lock (in-memory).
- **Idempotency replays the original result — breaking for custom stores.** `IIdempotencyStore.TryClaimAsync` returns an
  `IdempotencyClaim` (`Claimed` / `InProgress` / `Completed` + the stored result), and `CompleteAsync(key, result)`
  records the outcome. A duplicate of a completed request **returns what the first one returned**: a plain
  `CommandResult` out of the box, and any other result type once you opt in with
  `UseIdempotency(i => i.ReplayResultsWith(jsonSerializerOptions))` (resolved type metadata only, so it stays
  Native-AOT-safe with a source-generated `JsonSerializerContext`) or your own `IIdempotencyResultSerializer`. A
  duplicate that arrives while the original is still running throws `DuplicateRequestException` with
  `IsInProgress = true` - `CQRSharp.AspNetCore` maps that to `409 Conflict` + `Retry-After`. Completion and release are owner-checked in every store.
- **Queue shutdown honours `ShutdownTimeout`, and drains.** In-flight background work items were handed the host's
  stopping token, so they were cancelled the instant shutdown began — the opposite of the documented grace period. They
  now keep running for `ShutdownTimeout`. The queue also refuses new work as soon as shutdown begins and **runs the
  backlog that is still queued** inside the same budget; what the budget does not cover is cancelled rather than left
  pending, so a caller awaiting a queued item never hangs. `BackgroundTaskQueueOptions.DrainOnShutdown = false` restores
  cancel-the-backlog-immediately.
  A host that stops *during startup* closes the queue too: since .NET 10 a `BackgroundService` schedules its
  `ExecuteAsync` rather than running it inline, so the consumer's loop — and the shutdown in its `finally` — might never
  run, which left the queue accepting work nothing would ever execute.
- **Legacy module surface removed — breaking for hand-written modules.** `HandlerInvokerDelegate` and the boxed
  `object`-returning invokers are gone; `ICqrsModule.HandlerInvokers` / `IHandlerRegistry.TryGetInvoker` carry the typed
  delegates the generator emits.
- **Redis outbox — breaking defaults.** The default `KeyPrefix` is `{cqrsharp:outbox}:` (was `cqrsharp:outbox:`): the
  hash tag keeps every key the Lua scripts touch in one slot, so the store works on Redis Cluster. *Messages a 4.x
  deployment stored under the old prefix are not seen* — drain the outbox before upgrading or set the old prefix
  explicitly. The default `VisibilityTimeout` is 5 minutes (was 30 s): one lease covers a whole sequentially processed
  batch, so 30 s let a second instance re-deliver the tail of a batch that was still being worked through.
- **Repository layout.** Projects live flat under `src/` (the `Domain` / `Application` / `Infrastructure` /
  `Integrations` / `Presentation` layer folders are gone) and the samples moved to `samples/`. Package ids, assembly
  names and namespaces are unchanged.

### Fixed

- **`CQRSharp.EntityFrameworkCore`: the idempotency store was a singleton injected with the scoped `DbContext`.** Under
  scope validation (the ASP.NET Core Development default) the host failed to start; otherwise one root context served —
  and tracked every claim for — the whole process. The store now resolves a fresh context per operation, which also
  means a claim or release can never flush your own pending changes.
- **Outbox processor.** A notification handler's own `OperationCanceledException` (an HTTP timeout) escaped the
  `BackgroundService`, which stops the host by default, and — the attempt never having been recorded — did so again on
  every lease expiry without ever dead-lettering the message. It is now an ordinary failed attempt. Each message is also
  dispatched in **its own DI scope**, so one handler's half-tracked `DbContext` state cannot be committed by the next
  handler or break the store's bookkeeping for the rest of the batch.
- **Unit of work.** Rollback used the caller's (typically already cancelled) token, leaving the transaction open for
  every later request in the scope; it now uses `CancellationToken.None`. The streaming unit of work did not roll back
  when the outbox save or the commit failed, and its tracing span ended before the stream was enumerated.
- **EF Core stores** detach an entity whose save failed, so a stale row no longer poisons every later write on that
  context. **Redis outbox:** a late mark can no longer flip a dead-lettered message to processed (or back), and the
  never-read, never-expiring `done` set — one id per message, forever — is gone.
- **Idempotency release is owner-checked** in the Redis store (per-claim token, compare-and-delete) and the EF Core store:
  a claimant whose claim expired mid-flight and was taken over can no longer delete its successor's live claim.
- **The EF Core tables no longer grow without bound.** Processed outbox messages are purged after
  `EfCoreOutboxStoreOptions.ProcessedRetention` (default 7 days; dead-lettered messages are kept; `null` keeps
  everything) and expired idempotency keys are swept — both opportunistically, from the stores' normal operation.
- **Streaming requests get idempotency enforcement.** Only the command/query behavior was ever registered, so an
  `IIdempotentRequest` stream was silently unprotected. `StreamIdempotencyBehavior` keeps the claim only if the stream
  runs to completion.
- **`CQRSharp.Redis`** rejects two different connection strings instead of silently using the first for both stores.
- **In-memory stores.** Two pollers could both claim a message whose lease had expired; processed outbox messages and
  expired idempotency claims were never evicted (contrary to the docs).
- **Registration is idempotent.** Calling `AddCqrsGenerated(...)` twice on one collection — a library's own wiring plus
  its host's — registered behaviors and handlers twice: every idempotent request was rejected as its own duplicate and
  notifications were handled twice.
- **Metrics:** `cqrsharp.queue.items.current` drifted upward on every `DropNewest` eviction.
- **`NotificationDispatcher`** cached "has a stable name" in a process-wide static, so two hosts in one process (tests,
  multi-tenant) shared the first host's answer.

#### Source generator and analyzers

- A **`partial` handler or request** declared across files produced duplicate candidates: a false CQRGEN004 "multiple
  handlers", duplicate dispatcher arms (CS8510) or a false duplicate-name CQRGEN002.
- An **array result type** (`QueryBase<UserDto[]>`, `byte[]`) was treated as inaccessible and its binding silently
  dropped — dispatch threw "no handler" at runtime. Constructed generics are now checked through their type arguments,
  and `file`-local types are reported instead of emitted.
- **AOT hints** closed every open-generic behavior over every request without checking constraints, so a
  `where TRequest : ICommand` behavior next to any query broke the build (CS0311). Hints are emitted only for pairs that
  satisfy the constraints; record requests and `ICommand<TResult>` are now covered.
- The generated stream dispatcher relied on the consumer's implicit usings (`ImplicitUsings` disabled ⇒ CS1061).
- The **outbox serializer** ignored inherited properties (an abstract `DomainEvent`'s `EventId` came back `default`) and
  read every enum with `GetInt32`, so a `long`/`uint`/`ulong`-backed value outside the `int` range made its message
  undeliverable.
- **Interceptor attributes** were rebuilt from constructor arguments only: named arguments were dropped, `params`/array
  arguments became `null`, and strings, chars and non-`int` numerics rendered as invalid or wrong code.
- A handler for a **base notification type** (`INotificationHandler<INotification>`) next to a derived one emitted a
  subsumed switch arm (CS8510); so did a **concrete request deriving from another concrete request**.
- **CQRA003 / CQRA001** ignored `IResultCommandHandler<,>`, so a same-project value-returning command was reported as
  having no handler.

### Added

- **`CQRSharp.AspNetCore`** — maps what a dispatch throws or returns onto HTTP: validation failures to a 400
  ProblemDetails carrying the errors, rate limiting to 429, timeouts to 504, a duplicate in progress to 409 + `Retry-After`,
  and a `CommandResult` to an `IResult` (`ToHttpResult()` / `ToCreatedHttpResult(...)`: 204 / 200 / 201, or a
  ProblemDetails on failure); plus `Idempotency-Key` header reading and validation. Native-AOT-safe. See
  `docs/aspnetcore.md`.
- **`CQRSharp.FluentValidation`** — `UseFluentValidation()` runs your `AbstractValidator<T>`s inside the validation
  behavior; they can be mixed with native `IRequestValidator<T>`s on the same request. See `docs/fluentvalidation.md`.
- **`CQRSharp.Testing`** — the `OutboxStoreContractTests` / `IdempotencyStoreContractTests` suites every built-in store
  passes, for checking a custom store, and `RecordingCqrsDispatcher`, a stub-and-record `ICqrsDispatcher` for unit
  tests of code that dispatches. See `docs/testing-package.md`.
- **`CQRSharp.Templates`** — `dotnet new install CQRSharp.Templates`, then `dotnet new cqrsharp -n MyApp`. (The
  template existed in 4.1.0 but was never packed.)
- **Metrics.** A `CQRSharp` meter: `cqrsharp.request.duration` (histogram; type / kind / outcome - a *returned* failed
  `CommandResult` and an abandoned stream count as failures), `cqrsharp.notifications.published`,
  `cqrsharp.outbox.messages` and `cqrsharp.outbox.dispatch.duration` (`processed` / `retry` / `dead_letter` /
  `claim_lost`). `CqrsTelemetry` publishes every activity-source, meter and instrument name, so OpenTelemetry wiring is
  `AddSource(CqrsTelemetry.ActivitySourceNames)` / `AddMeter(CqrsTelemetry.MeterNames)`. Pay-for-use: nothing is
  measured until a listener subscribes, and an unmetered dispatch still takes the fast path.
- `RequestContextBase(DateTime createdAt)` — the default context factory stamps `CreatedAt` from the injected
  `TimeProvider`, so request contexts follow a fake clock in tests like everything else.
- `CommandResult<T>` has a public constructor (result replay and hand-rolled serializers need one).
- `BackgroundTaskQueueOptions.DrainOnShutdown`, `AspNetCore`'s `DuplicateInProgressRetryAfter`.
- `IQueueMetricsReporter.ItemEvictedNewest()` (default-implemented, so existing reporters keep compiling).
- `OutboxMessageFactory` — the one place a buffered notification becomes an `OutboxMessage`.
- `ICqrsModule.TypedHandlerInvokers` / `RequestRoutes` / `UntypedRequestRoutes`, `IHandlerRegistry.TryGetTypedInvoker`,
  `IContextFactoryRegistry.TryGetResolver`, and the `RequestRoute` delegates — all default-implemented.
- `UnitOfWorkOptions.RollbackOnFailedResult`, `EfCoreOutboxStoreOptions.ProcessedRetention` / `PurgeInterval`,
  `StreamIdempotencyBehavior`.
- `benchmarks/CQRSharp.Benchmarks` — BenchmarkDotNet dispatch comparison against MediatR and Mediator.

#### Packaging and project

- **The packages ship `net8.0`, `net9.0` and `net10.0` builds.** The release pipeline installed only the 8.0 SDK, and
  the libraries multi-target only as far as the installed SDK allows, so earlier packages carried `net8.0` alone.
- `CQRSharp.Pipelines` ships its XML documentation (it was the one package without IntelliSense docs).
- **Public API tracking** (`PublicAPI.Shipped.txt` per library — an unrecorded API change fails the build) and
  **package validation** on pack.
- One validation gate shared by CI and the release pipeline: zero-warning build, tests on all three frameworks against a
  real Redis with coverage, the sample as a Native AOT binary, pack, package-content checks, and a smoke test that
  scaffolds the template against the freshly packed packages. CI also builds and tests on Windows and macOS. The
  release publishes exactly the artifact that gate produced.
- Central package management (`Directory.Packages.props`), a root `.editorconfig`, a roll-forward `global.json`,
  Dependabot, `CONTRIBUTING.md`, `SECURITY.md`, a code of conduct, issue forms and a pull-request template.

### Known limitations

- **Redis Cluster:** a *custom* outbox `KeyPrefix` must keep a hash tag (`{...}`) for the store to work on a cluster.
- A *custom* request context that does not pass a timestamp to `RequestContextBase(DateTime)` still stamps `CreatedAt`
  from the system clock (the contracts assembly targets netstandard2.0 and cannot see `TimeProvider`).
- The `CQRSharp.Testing` contract suites are xUnit v2 (`RecordingCqrsDispatcher` has no test-framework dependency).

## [4.2.1]

### Fixed

- **`UseTimeProvider(factory)` self-reference no longer hangs the host.** A factory passed to
  `UseTimeProvider(Func<IServiceProvider, TimeProvider>)` that resolves `TimeProvider` from the provider — e.g.
  `sp => sp.GetService<TimeProvider>() ?? TimeProvider.System` — is self-referential: that factory *is* the
  `TimeProvider` registration, so resolving `TimeProvider` inside it recursed until the DI container deadlocked (the
  `?? TimeProvider.System` fallback is unreachable dead code). It only surfaced when something first resolved the clock,
  so unit tests that inject a clock directly never caught it. The factory is now guarded and **fails fast with a clear
  `InvalidOperationException`** on first resolution instead of deadlocking. Note: to make CQRSharp defer to a
  `TimeProvider` your host already registered, don't call `UseTimeProvider` at all — `AddCqrs` registers
  `TimeProvider.System` with `TryAdd`, so an existing registration wins.

## [4.2.0]

### Changed

- **Post-handlers are uniformly outcome-aware — breaking.** `IPostHandlerAttribute.OnAfterHandle` now takes the
  request's `RequestOutcome` (the value the handler returned, or the exception it threw) and runs on **both** the success
  and the exception paths — previously it received only the request and ran on success only. This collapses 4.1.0's
  opt-in `IPostHandlerOutcomeAware` / `OutcomeAwarePostHandlerAttribute` into the single post-handler contract: there is
  no longer an outcome-blind post-handler.
  **Migration:** add a `RequestOutcome outcome` parameter to your `OnAfterHandle` implementations —
  `OnAfterHandle(IRequest request, RequestOutcome outcome, IServiceProvider sp, CancellationToken ct)` — and read
  `outcome.Result` (the returned value, cast to the request's result type) or `outcome.Exception` to classify on what
  happened. A post-handler now also runs when the handler threw; it is isolated, so a fault in it cannot mask the
  original exception.

### Removed

- `IPostHandlerOutcomeAware` and `OutcomeAwarePostHandlerAttribute` (added in 4.1.0) — folded into `IPostHandlerAttribute`.

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
