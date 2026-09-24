# Changelog

All notable changes to CQRSharp are documented here. This project adheres to [Semantic Versioning](https://semver.org/).

## [5.0.0]

A major release, measured here against 4.2.1. The authoring surface moves to three namespaces; the outbox delivers
per handler, in order per partition key, with leases, an inbox, dead-letter operations and backlog metrics; idempotent
requests replay their original result and reject a reused key with a different payload; `CommandResult` carries a
typed error kind; the unit of work is one explicit contract with an EF Core implementation; and several ways a
notification could be lost without an error are closed. New packages: `CQRSharp.AspNetCore`,
`CQRSharp.FluentValidation`, `CQRSharp.Testing`, `CQRSharp.Testing.Xunit.V3` and `CQRSharp.Templates`.

Every assembly that runs the CQRSharp source generator must be rebuilt against 5.0.0: generated code binds to 5.0
runtime types, and `ICqrsModule` is written by the generator only (hand-written modules and registries are not
supported).

### Upgrading from 4.2.1

Before you deploy:

1. **Drain the outbox with 4.x processors.** A 5.0 outbox message is addressed to one handler; a message stored by 4.x
   has no handler name and cannot be delivered, in any store and under any key prefix. Upgrade once the 4.x backlog is
   empty. For Redis, the old keys (`cqrsharp:outbox:*` by default) can then be deleted.
2. **Let in-flight Redis idempotency claims expire.** The Redis idempotency key layout changed (see step 30), so
   5.0 does not see claims made by 4.x.

Packages:

3. `CQRSharp.Core` no longer depends on `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Logging.Console` or
   `Microsoft.Extensions.DependencyInjection`. An app that calls `Host.CreateDefaultBuilder` /
   `Host.CreateApplicationBuilder` or `AddConsole()` references `Microsoft.Extensions.Hosting` itself (the Worker and
   ASP.NET Core project templates already do).
4. `CQRSharp.EntityFrameworkCore` builds each target framework against its own EF Core major: `net8.0` against EF Core 8
   (`[8.0.10, 9.0.0)`), `net9.0` against EF Core 9 (9.0.2 or later), `net10.0` against EF Core 10 (10.0.0 or later). A
   `net8.0` app on EF Core 9 must target `net9.0`; restore reports it (NU1107, or NU1608 when the app references EF Core
   directly).

Namespaces:

5. Replace the `CQRSharp.Abstractions.*` usings, and the `CQRSharp.Core.*` ones that name everyday types, as follows:
   - **`CQRSharp`**: requests and their base types, handler interfaces, `CommandResult`, notifications and the lifecycle
     notification types, `IRequestContext` / `RequestContextBase` / `IRequestContextFactory<TContext>`, interceptor
     contracts, validators and `ValidationFailure`, exception hooks, the idempotency / retry / transactional markers,
     `ICqrsDispatcher`, `AddCqrsGenerated`, the dispatcher, queue, notification and outbox options and enums, and the
     exceptions a caller catches (`DuplicateRequestException`, `RequestValidationException`,
     `RateLimitExceededException`, …).
   - **`CQRSharp.Pipelines`**: `ICqrsBuilder`, the built-in behaviors and their options, `IPipelineBehavior`,
     `IStreamPipelineBehavior`, `INotificationPipelineBehavior` and their delegates, `IPrioritizedPipelineBehavior`,
     `CqrsPipelinePriorities`, `IRateLimitedContext`, `OutboxStoreBuilder` and `IdempotencyStoreBuilder`.
   - **`CQRSharp.Persistence`**: the contracts a store or unit-of-work implementation writes against (`IUnitOfWork`,
     `IOutboxStore`, `IInboxStore`, `OutboxMessage`, `IIdempotencyStore`, `IIdempotencyResultSerializer`,
     `INotificationSerializer`, …).
   - **`CQRSharp.EntityFrameworkCore`** replaces its `.Persistence` and `.Extensions` namespaces; **`CQRSharp.Redis`**
     replaces `.Outbox` and `.Idempotency`. Registration extensions stay in `Microsoft.Extensions.DependencyInjection`.
   - Runtime extension points: `CQRSharp.Core.Background.TaskQueue` → `CQRSharp.Core.BackgroundTasks`,
     `CQRSharp.Core.Background.Outbox` → `CQRSharp.Core.Outbox`, `CQRSharp.Core.Caching.*` → `CQRSharp.Core.Registries`,
     the source-generation attributes → `CQRSharp.Core.SourceGeneration`.

   With the `CQRSharp` meta-package and `ImplicitUsings`, `CQRSharp` and `CQRSharp.Pipelines` are global usings
   (`<CQRSharpImplicitUsings>false</CQRSharpImplicitUsings>` opts out). Renamed on the way: `RateLimiter` →
   `RequestRateLimiter`, `RateLimiterOptions` → `RateLimitingOptions`, `RedisIntegrationServiceCollectionExtensions` →
   `RedisOutboxStoreServiceCollectionExtensions`, `RedisIdempotencyServiceCollectionExtensions` →
   `RedisIdempotencyStoreServiceCollectionExtensions` (extension-method call sites are unaffected).

Registration:

6. `AddCqrsGenerated` has two overloads, `AddCqrsGenerated()` and `AddCqrsGenerated(Action<ICqrsBuilder>)`. Replace
   `AddCqrsGenerated(configureQueue:, configureOutbox:, configureDispatcher:, configureValidation:)` with the builder
   (`b => b.ConfigureQueue(…).ConfigureDispatcher(…).UseOutbox(…).ValidateOnStart()`) or with
   `services.Configure<T>(…)`. `AddCqrs` is parameterless and exists for generated code; the generated `AddGenerated()`
   is gone.
7. `UsePipelinePack` and `CqrsPipelinePackOptions` are removed. Each builder verb registers its own behavior only.
   **Both forms of `AddCqrsGenerated` register the validation and exception-handling behaviors**, the parameterless
   one included (it is `AddCqrsGenerated(_ => { })`): an existing validator or exception hook starts running once you
   upgrade. They do nothing for a request without validators or exception hooks; turn them off with
   `UseValidation(false)` / `UseExceptionHandling(false)`. A project that references only `CQRSharp.Core`, without
   `CQRSharp.Pipelines`, has only the parameterless form, and it registers no pipeline behavior.
8. `AddOutboxProcessor` is removed: the processor is always registered and idles while the outbox is disabled. Tune
   it with `UseOutbox(o => o.ConfigureProcessor(…))` or `services.Configure<OutboxProcessorOptions>(…)`.
9. Startup validation is off unless you ask for it, on every entry point: `CqrsStartupValidationOptions.Policy`
   defaults to `Off` (4.2.1's direct `AddCqrsGenerated(...)` overload defaulted to `ThrowOnError`). Call
   `ValidateOnStart()` to fail fast. `CQRCONF005` (an `IIdempotentRequest` without `UseIdempotency`) and `CQRCONF007`
   (`Transactional` outbox without a unit of work) are errors now.
10. The bindings health check is removed (`AddCqrsBindings`, `CqrsBindingsHealthCheck`,
    `CqrsBindingIssueSeverity.Info`). Use `ValidateOnStart()` (or `ValidateOnStart(CqrsValidationPolicy.WarnOnly)` to
    only log) as the configuration gate; `AddHealthChecks().AddCqrsOutbox()` checks the outbox.

Requests, handlers and contexts:

11. Handler interfaces take no context type argument: `ICommandHandler<TCommand>`, `IQueryHandler<TQuery, TResult>`,
    `IResultCommandHandler<TCommand, TResult>`, `IStreamRequestHandler<TRequest, TItem>`. `request.Context` is typed by
    the request's base class.
12. `ICommandInterceptor` is removed: implement `IPreHandlerAttribute` and `IPostHandlerAttribute`.
13. `IRequestContextFactory<TContext>` has one member, `ValueTask<TContext> CreateContextAsync(IRequest request,
    CancellationToken cancellationToken)`; a synchronous factory returns `new(context)`. The non-generic
    `IRequestContextFactory`, `AsyncRequestContextFactory<TContext>` and `DefaultRequestContextFactory` are removed; to
    replace the default context's factory, register an `IRequestContextFactory<RequestContextBase>`.
14. The dispatcher always builds a request's `Context` from its factory and replaces any context set before `Send` /
    `Stream`, so a bound request body cannot supply identity. `RequestBase<TContext>.Context` has no public setter;
    handler unit tests set it through `((IRequest)request).Context = …`.
15. `IRequest.Metadata` and `RequestBase.Metadata` are removed (a dispatched request serializes with System.Text.Json).
    `RequestMetadata` is a hidden registry type in `CQRSharp.Core.Registries`; read bindings through
    `ICqrsDiagnostics.DescribeRequest`.
16. `ITransactionalCommand` no longer derives from `ICommand`, nor from any request interface: it only opts a command,
    outcome-only or value-returning, into a transaction. A type that implemented only `ITransactionalCommand` adds
    `ICommand` (or `ICommand<TResult>`) or derives from `CommandBase` / `ResultCommandBase<T>`. On a query or stream it
    commits but does not make the request a command (the new `CQRA015` warns); a query or stream that writes uses
    `ITransactionalQuery` with `IsReadOnly = false`. A request counts as a command, for its lifecycle notifications,
    span name and metric, when it implements `ICommandMarker` and is dispatched with a `CommandResult` or
    `CommandResult<T>`. `IsolationLevel` is get-only on `ITransactionalCommand` and `ITransactionalQuery`.
    `ITransactionalQuery<TResult>` is removed; use `QueryBase<TResult>, ITransactionalQuery`.
17. `CommandResult`'s protected constructor is `(bool isSuccess, CommandErrorKind errorKind, string? errorMessage,
    int? errorCode, IReadOnlyList<ValidationFailure>? validationFailures)`. `ToString()` includes the kind and omits an
    absent code (`Command failed [NotFound]: …`). A success carries no error message, code or validation failures.

Notifications:

18. A notification reaches the handlers of its **runtime** type, whatever type it is published as: every generated
    handler declared for that type, a base class or an interface of it (each once, at its nearest declared type), plus
    the handlers registered by hand as `INotificationHandler<TRuntimeType>`. In 4.2.1 the handlers that ran depended on
    the static type a notification was published as, and a base-type handler ran only for a derived notification that
    had no handlers of its own.
19. Generated notification handlers are registered by their concrete type only, not as `INotificationHandler<T>`, so
    `IEnumerable<INotificationHandler<T>>` returns only the handlers you register yourself; the generated subscriptions
    are internal to the runtime. Publish through `ICqrsDispatcher.Publish` rather than invoking handlers you resolved.
20. `INotificationPipelineBehavior<T>.Handle` takes a `NotificationHandlerDelegate next`; call `await next()` or
    `await next(cancellationToken)`.
21. Lifecycle notifications: `Command*Notification.Command` is an `ICommandMarker`, because value-returning commands
    (`ICommand<TResult>`) now publish `CommandInitiated` / `CommandCompleted` / `CommandFailed` too (4.2.1 published
    none for them); their `CommandCompletedNotification.Result` is the `CommandResult<T>`.
    `QueryCompletedNotification<T>.Result` is a `TResult`. `TaskEnqueuedNotification` and `TaskRejectedNotification` are
    removed (see step 34).
22. `INotificationSerializer.GetNotificationName` is replaced by `bool TryGetNotificationName(Type, out string?)`, which
    returns `false` for a non-durable type (there is no `Type.FullName` fallback). `IStableNotificationNameProvider` is
    removed. `AddNotificationSerializer<T>()` replaces the generated serializer entirely, before or after
    `AddCqrsGenerated`, so it must name and serialize every durable notification.
23. `ICqrsDispatcher` is the one dispatch seam. The notification dispatchers (`NotificationDispatcher`,
    `DirectNotificationDispatcher` and their interfaces `INotificationDispatcher`, `IDirectNotificationDispatcher`) are
    removed: publishing goes through one internal publisher per service provider, which is handed the publishing scope
    on every call, so nothing is resolved or allocated per scope to publish. The subscription registry and the pipeline
    executor are internal, and `IPipelineExecutor`, `IRequestDispatcher` and `IStreamRequestDispatcher` do not exist:
    send, stream and publish through `ICqrsDispatcher`, and wrap dispatch with pipeline behaviors rather than by
    replacing a service in the container.

Unit of work:

24. `IUnitOfWork` (`CQRSharp.Persistence`) is the one contract: `HasActiveTransaction`, `BeginTransactionAsync(level,
    ct)`, `CommitAsync(ct)` (saves every pending change, then commits) and `RollbackAsync(ct)` (discards every pending
    change; never throws when no transaction is active). `IExplicitUnitOfWork`, `SaveChangesAsync`, `GetService<T>`, the
    savepoint methods and `ITransactionContext` are removed, and so is the implicit `SaveChanges`-only mode. With EF
    Core, register `UseEntityFrameworkCoreUnitOfWork<TContext>()`.
25. `UnitOfWorkOptions.DefaultIsolationLevel` defaults to `Unspecified` (the data store's default; it was
    `ReadCommitted`). A request whose `IsolationLevel` is unset or `Unspecified` uses it.
26. A command that **returns** a failed `CommandResult` is a failed request: its unit of work rolls back, its
    notifications are discarded and its idempotency claim is released. `UnitOfWorkOptions.RollbackOnFailedResult =
    false` commits instead (and then keeps the idempotency key, so a duplicate gets the same failed result).
27. An `ITransactionalQuery` whose `IsReadOnly` is `true` is rolled back when it completes.

Outbox:

28. `UseOutbox(...)` defaults to `OutboxMode.Enabled` (it was `Transactional`). Call `.Transactional()` to store only
    what is published inside an active unit-of-work transaction; it needs a registered unit of work. `IOutbox` is
    removed: publish through `ICqrsDispatcher.Publish`.
29. **EF Core schema.** Add a migration (`dotnet ef migrations add CQRSharp5`). The outbox table is keyed by a
    database-generated `Sequence` (`Id` becomes an alternate key) and gains `HandlerName`, `PartitionKey`,
    `NotificationId` and `FailedAt`, and its indexes change; `RowVersion` becomes an application-managed integer
    concurrency token instead of a database row version. On SQL Server that turns a `rowversion` column into an integer
    one, which SQL Server cannot alter in place: review the generated migration and make that step drop and re-add the
    column (the outbox is drained, so no row depends on it). `ApplyCqrsOutbox()` also maps the `CqrsInboxRecords`
    table. The idempotency table gains `Completed`, `Result` and `Fingerprint`, an `ExpiresAt` index, and a
    450-character key (was 512). On SQL Server, map the key with a binary collation,
    `modelBuilder.ApplyCqrsIdempotency(IdempotencyEntityConfiguration.SqlServerBinaryCollation)`: keys are compared
    ordinally, and host start fails on a case-insensitive key column. Dead letters left by 4.x have no `FailedAt` and
    cannot be delivered; remove them with `PurgeDeadLettersAsync(DateTime.MaxValue)`, since they count in the
    dead-letter gauge and the outbox health check.
30. **Redis.** The default prefixes are `{cqrsharp:outbox}:` and `{cqrs:idemp}:`, and a `KeyPrefix` must contain a
    non-empty hash tag (`{…}`), on a single node too: host start fails otherwise, so the 4.x prefixes are rejected.
    `FinalizedRetention` is removed: a processed message is deleted when it is marked processed (dead letters have
    `DeadLetterRetention`, inbox records `InboxRetention`). The default `VisibilityTimeout` is 5 minutes (was 30 s).
    CQRSharp no longer registers an `IConnectionMultiplexer` in the container: an app that resolved one only because a
    Redis store registered it registers its own.
31. **Handler names.** Each notification handler has a stable name that outbox messages are addressed to, by default
    its namespace-qualified type name. Pin it with `[NotificationHandlerName("…")]` before you rename or move a handler
    that stored messages may still address.
32. `OutboxProcessorOptions.MaxRetryAttempts` is renamed `MaxAttempts` (the total number of attempts, the first
    included; default 3).
33. **Custom stores.** `IOutboxStore` is claim-based (`ClaimPendingAsync` returns `ClaimedOutboxMessage`s; marks,
    `RenewAsync`, `ReleaseAsync` and `DeferAsync` take the `OutboxClaim` and do nothing once it is lost) and gains the
    dead-letter operations, `GetBacklogAsync` and `JoinsUnitOfWork`; `OutboxMessage` gains `HandlerName`,
    `PartitionKey`, `NotificationId` and `FailedAt`. A store ships an `IInboxStore` beside it. `IIdempotencyStore` is
    `TryClaimAsync(key, fingerprint, ct)` → `IdempotencyClaim`, `CompleteAsync(key, claimToken, result, ct)` and
    `ReleaseAsync(key, claimToken, ct)`. Check an implementation with the suites in `CQRSharp.Testing.Xunit.V3`
    (`OutboxStoreContractTests`, `InboxStoreContractTests`, `IdempotencyStoreContractTests`; xUnit v3).

Background queue, behaviors and observability:

34. **Background queue.** `BackgroundTaskQueueOptions.EnableMetrics`, `CallbackChannelCapacity`,
    `NotificationMaxRetries` and `NotificationRetryDelay`, `IQueueMetricsReporter`,
    `OpenTelemetryQueueMetricsReporter`, `QueuedTask` and `QueueWriteResult(Code)` are removed. Work the queue does not
    run faults its caller with `BackgroundTaskRejectedException` (`Reason`: `QueueFull`, `Evicted`, `QueueClosed`)
    instead of `ChannelClosedException` / `ObjectDisposedException`. `ShutdownTimeout` defaults to 20 s (was 30 s).
35. **Rate limiting.** `IRateLimitedContext` is `string UserId { get; }` (`RequestId` is gone).
    `RateLimitScope.PerCommand` is `PerRequestType`. `RequestRateLimiter.TryAcquire(userId, requestType, out
    retryAfter)` replaces `AllowRequest`. `RateLimitExceededException(string message, TimeSpan? retryAfter = null)`
    carries `RetryAfter` and no ids in its message.
36. **Timeouts and retries.** `UseTimeout` throws `RequestTimeoutException` (a `TimeoutException`, so existing catches
    still work). `ResilienceOptions` is validated at host start (`MaxRetries >= 0`, `MaxDelay >= BaseDelay`, a finite
    `BackoffMultiplier >= 1`; a 4.x `MaxDelay = 0` meaning "no cap" now fails), and `ComputeRetryDelay` is no longer
    public.
37. **Tracing.** Span attributes are renamed: `cqrsharp.request_type` → `cqrsharp.request.type` (now `Type.ToString()`),
    `cqrsharp.notification_type` → `cqrsharp.notification.name`, `db.isolation_level` →
    `cqrsharp.transaction.isolation_level`, `resilience.max_retries` / `resilience.retry_delay_ms` →
    `cqrsharp.resilience.max_retries` / `cqrsharp.resilience.retry_delay_ms`, `ratelimit.user_id` →
    `cqrsharp.ratelimit.user_id` (`ratelimit.request_id` is gone). The "CQRS Outbox Dispatch" span is
    `ActivityKind.Consumer` (was `Producer`). `CqrsActivitySource` is internal; use `CqrsTelemetry.ActivitySourceNames`.
38. **Queue metrics.** The queue meter (`CQRSharp.Core.BackgroundTasks`) measures whenever a listener is attached, with
    new instruments, and is created through the provider's `IMeterFactory` when one is registered:
    `cqrsharp.queue.depth`, `.enqueued`, `.evicted`, `.rejected` and `.wait.duration`. The 4.x
    `cqrsharp.queue.items.*` and `cqrsharp.queue.item.latency.seconds` instruments are gone.
39. **Logging.** Every log line has an event id from its component's block (listed in
    [docs/observability.md](docs/observability.md)), and levels follow who has to act: a failed request is logged at
    `Error`, with its exception, only by `UseLogging()`; caller cancellations and rejections the caller must fix
    (validation, duplicate, key mismatch, rate limit) are `Information`; timeouts and queue refusals `Warning`. The
    outbox processor logs per-message lines at `Debug`, a failed attempt at `Warning` (was `Error`) and a dead letter at
    `Error` (was `Critical`); the queue logs start and stop at `Debug`. Adjust filters and alerts keyed on the old
    levels.
40. **Diagnostics.** The analyzers `CQRA001`, `CQRA007`, `CQRA009`, `CQRA013` and `CQRA017`, the generator diagnostic
    `CQRGEN008`, the configuration checks `CQRCONF002` and `CQRCONF008`, and the binding check `CQRDIAG002` are removed
    (suppressions of them are harmless). The generator's
    `cqrsharp_generator.suppress_missing_request_handler_diagnostics` switch is removed: silence `CQRGEN003` with
    `<NoWarn>` or `dotnet_diagnostic.CQRGEN003.severity`.
41. **Internal types.** Types that existed for the runtime's own wiring are internal or hidden from IntelliSense,
    among them `CqrsDispatcher` (resolve `ICqrsDispatcher`), `PipelineExecutor`, the request / handler / context-factory
    registries, `RequestExceptionHookRegistry`, `CqrsConfigurationInspector`, `ICqrsNotificationRegistry` (removed),
    `HandlerInvokerDelegate` (removed), the dispatch seams of step 23, and `EfCoreOutboxStore<TContext>` (register it
    with `AddEntityFrameworkCoreOutboxStore<TContext>()` or `UseOutbox(o => o.UseEntityFrameworkCore<TContext>())`).
    `OutboxStoreBuilder` and `IdempotencyStoreBuilder` have no public constructor: the `UseOutbox` / `UseIdempotency`
    verbs create them.

### Changed

**Dispatch**

- Dispatch cost is lower: a per-provider request plan caches the registry lookups and sorted interceptors and skips
  every stage with nothing registered; the generator emits typed handler invokers and exact-type route tables; a DI
  scope no longer builds a table of every request type. Lifecycle notifications, tracing and metrics are pay-for-use:
  a lifecycle notification is published only when a handler or notification behavior subscribes to it. See
  [benchmarks/README.md](benchmarks/README.md).
- Once `*Initiated` is published, exactly one of `*Completed` or `*Failed` follows, for commands, queries and streams.
  `*Failed` covers a throwing pre-handler, handler or post-handler, caller cancellation and timeouts; it is delivered
  with `CancellationToken.None`, and a subscriber that throws is logged and never replaces the request's exception.
  `*Completed` is published after the post-handlers succeed. A stream whose consumer stops enumerating early, without
  an error, publishes neither and runs no post-handlers, and its span ends with status `Error`. A subscriber of
  `*Initiated` that throws fails the request before its work begins, with no terminal notification. Every post-handler
  runs, like nested `finally` blocks; streams follow the post-handler contract of 4.2.0.
- The context factory is resolved from the caller's scope and runs on the caller's flow, before a request is queued
  (`RunMode.Queued`) or given its own scope (`ExecutionScopeMode.New`), so a factory reading a scoped current user or
  `IHttpContextAccessor` sees the caller. `RequestContextBase.CreatedAt` is stamped from the application's
  `TimeProvider`.
- One context factory serves a context type: one registered by hand wins, then the composition root's discovered one,
  then the module registered last. `AddCqrsGenerated()` registers referenced assemblies' modules first and the calling
  assembly's last, so the host serves a request both it and a library handle. Exception hooks declared in several
  assemblies all run, each once: every matching action first, then handlers from the most derived exception type.
- Library code never resumes on the caller's `SynchronizationContext`, so a caller that blocks on a dispatch from a UI
  or other single-threaded context does not deadlock.

**Registration**

- Every explicit store registration replaces the registered store, whatever its order relative to `AddCqrsGenerated`;
  the last explicit choice wins (in 4.2.1 the first registration won). The outbox and inbox stores are replaced as a
  pair. A bare `UseOutbox(...)` / `UseIdempotency()` falls back to the in-memory store only when no store of that kind
  is registered.
- Calling `AddCqrsGenerated` more than once on a collection (a library's wiring and its host's) adds up: each behavior
  and handler is registered once, and every call's verbs take effect. Repeated configuration verbs, the outbox
  builder's `ConfigureProcessor` included, all run in call order. Generated registrations use `TryAdd*`, so a
  lifetime you chose for a handler stands and a hand registration is not duplicated.
- The generator registers every non-generic `IRequestValidator<T>`, `IRequestExceptionAction<,>`,
  `IRequestExceptionHandler<,,>`, `IRequestContextFactory<T>` and closed pipeline behavior (`IPipelineBehavior<,>`,
  `IStreamPipelineBehavior<,>`, `INotificationPipelineBehavior<T>`) it finds; register one by hand only when the
  generator cannot see it. Discovered validators, hooks and behaviors are keyed services (`DiscoveredServices.Key`): an
  implementation you register yourself replaces the discovered one of the same type.
- The startup validator runs before any hosted service starts, whatever the registration order.

**Outbox**

- Delivery is per handler: publishing a durable notification stores one message per subscribed handler, each with its
  own attempts, back-off and dead letter, so a failing handler never makes a healthy sibling run again. A notification
  nothing subscribes to stores nothing.
- Ordered delivery per partition key: `[NotificationName("…", PartitionBy = nameof(OrderId))]` or
  `IPartitionedNotification` delivers messages that share a key and a handler in publication order. Claims are FIFO by
  `CreatedAt` in every store.
- Delivery is claim-based: the processor leases each message, renews the lease before dispatch once half of it is
  gone, releases what it still holds on shutdown, and a processor whose lease was taken over cannot overwrite the new
  owner's outcome. Each message is delivered in its own DI scope. What a handler publishes during a delivery is stored
  only when the delivery succeeds.
- The processor claims batch after batch while messages are due and waits `PollingInterval` only after an empty poll;
  a message stored by this process wakes it at once. `MaxDegreeOfParallelism` delivers a batch concurrently.
- A step before the handler that fails (renewing the lease, checking the inbox, beginning the delivery's transaction)
  charges no attempt: the processor logs event 5028, counts the outcome `not_started`, and the message is claimable
  again once its lease runs out.
- Retries back off per `OutboxProcessorOptions.Retry` (2 s doubling to 5 minutes, ±20 % jitter). A message whose
  notification or handler this instance does not know is deferred, without counting an attempt, for
  `UnknownRecipientGracePeriod` (1 hour) so a rolling deploy can finish, then dead-lettered.
- A request settles its own notifications: they are stored when it succeeds and discarded when it fails, and a publish
  from outside any request is written straight to the store. With a unit of work, a store that joins the transaction
  (EF Core over the same context) is written inside it; any other store right after the commit.

**Idempotency**

- A duplicate of a completed request returns the original result: a plain `CommandResult` out of the box, any other
  result type through `UseIdempotency(i => i.ReplayResultsWith(jsonSerializerOptions))` (the options need a
  `TypeInfoResolver`, such as a source-generated `JsonSerializerContext`) or your own `IIdempotencyResultSerializer`.
  `DuplicateRequestException` remains for a duplicate that arrives while the original runs (`IsInProgress`) and for a
  result that cannot be replayed.
- A key reused with a different payload throws `IdempotencyKeyMismatchException`. The payload fingerprint is a SHA-256
  over the request type and its properties, rendered by the generator; implement `IFingerprintedRequest` to choose it.
  A request whose payload the generator cannot render is compared on the key alone and reported as `CQRGEN014`.
- Claims carry a token, and a store completes or releases a claim only while the key still carries that token.
- Streaming requests that implement `IIdempotentRequest` are protected by `StreamIdempotencyBehavior`; the claim is
  kept only if the stream runs to completion.

**Behaviors**

- Retries: only a cancellation of the caller's own token is terminal, so an `HttpClient` timeout
  (`TaskCanceledException`) or a dependency's `TimeoutException` is retried for an `IRetryableRequest`. Never retried:
  caller cancellation, `RequestTimeoutException`, `RateLimitExceededException`, `DuplicateRequestException`,
  `IdempotencyKeyMismatchException` and `RequestValidationException`.
- Exception hooks: only the caller's own cancellation bypasses them. Any other `OperationCanceledException` (an
  `HttpClient` timeout, a handler's linked token) is a failure the exception actions and handlers see.
- `RateLimitExceededException.RetryAfter` is the time until the caller's bucket holds a token, in whole milliseconds
  rounded up. `RateLimitingOptions.MaxEntries` bounds the buckets kept: at the limit, refilled buckets are dropped
  first, then the least recently used.

**Background queue**

- `ConsumerCount` is the number of work items that run at once, each on the thread pool.
- A caller's token withdraws a queued item at once; the handler never runs.
- Shutdown refuses new work, lets running items finish and runs the remaining backlog within `ShutdownTimeout`
  (`DrainOnShutdown = false` cancels the backlog instead); what the budget does not cover is cancelled, so an awaiting
  caller never hangs. In 4.2.1 running items got the host's stopping token and were cancelled as shutdown began.
- A request sent from inside any queued work item runs at once in a scope of its own instead of queueing behind its
  caller, which deadlocked with `ConsumerCount = 1`.
- `RunMode` governs `Send` only: `Stream(...)` runs on the flow that enumerates it in every run mode (it threw under
  `Queued`).

**Native AOT**

- Open-generic pipeline behaviors apply to requests whose result or streamed item is a value type, and open-generic
  notification behaviors to struct notifications: the generator emits closed factories for them (4.2.1 threw at the
  first dispatch). A registered behavior generated code cannot close fails the dispatch with an explanation and is
  reported as `CQRDIAG004`. The AOT hint generator is removed; the closed factories replace it.

### Added

- **`CQRSharp.AspNetCore`** — `AddCqrsProblemDetails()` maps what a dispatch throws to ProblemDetails (validation 400,
  duplicate 409 with `Retry-After` while in progress, key mismatch 422, rate limit 429 with `Retry-After`, timeout 504,
  queue refusal 503; each status configurable on `CqrsProblemDetailsOptions`); `ToHttpResult()` /
  `ToCreatedHttpResult(...)` map a `CommandResult` to 200 / 201 / 204 or to a ProblemDetails whose status follows the
  error kind; `GetIdempotencyKey()` / `TryGetIdempotencyKey()` read the `Idempotency-Key` header. See
  [docs/aspnetcore.md](docs/aspnetcore.md).
- **`CQRSharp.FluentValidation`** — `UseFluentValidation()` runs your `AbstractValidator<T>`s in the validation
  behavior, alongside `IRequestValidator<T>`s. See [docs/fluentvalidation.md](docs/fluentvalidation.md).
- **`CQRSharp.Testing`** — `RecordingCqrsDispatcher`, a stub-and-record `ICqrsDispatcher` for unit tests; no test
  framework dependency. **`CQRSharp.Testing.Xunit.V3`** — the outbox, inbox and idempotency store contract suites.
  See [docs/testing-package.md](docs/testing-package.md).
- **`CQRSharp.Templates`** — `dotnet new install CQRSharp.Templates`, then `dotnet new cqrsharp -n MyApp`
  (`--Framework net8.0|net9.0|net10.0`; the project references the `Microsoft.Extensions.Hosting` major of the chosen
  framework).
- **Typed errors on `CommandResult`.** `ErrorKind` (`Failure`, `Validation`, `NotFound`, `Conflict`, `Unauthorized`,
  `Forbidden`, `Unavailable`; `None` on success), the factories `NotFound`, `Conflict`, `Unauthorized`, `Forbidden`,
  `Unavailable`, `Invalid(…)` and `FromError(kind, …)`, and `ValidationFailures` as data. `CommandResult<T>` has the
  same factories and a public constructor.
- **Inbox.** Every outbox store ships an `IInboxStore` that records completed deliveries; the processor skips a
  redelivery it finds there (`OutboxProcessorOptions.UseInbox`, on by default). With the EF Core inbox and
  `UseEntityFrameworkCoreUnitOfWork<TContext>()` over the same context, the handler's changes and the inbox record are
  one commit.
- **Dead letters and backlog.** `IOutboxStore.GetDeadLettersAsync`, `RequeueAsync` and `PurgeDeadLettersAsync`;
  `GetBacklogAsync`; the gauges `cqrsharp.outbox.pending`, `cqrsharp.outbox.dead_letters` and `cqrsharp.outbox.lag`
  (sampled every `BacklogSampleInterval` while a listener is attached); the health check `AddCqrsOutbox()`
  (`OutboxHealthCheckOptions.MaxLag` / `MaxDeadLetters`, per registration).
- **Metrics.** The `CQRSharp` meter: `cqrsharp.request.duration`, `cqrsharp.notifications.published`,
  `cqrsharp.outbox.messages` and `cqrsharp.outbox.dispatch.duration` (outcomes `processed`, `duplicate`, `unrecorded`,
  `retry`, `deferred`, `dead_letter`, `claim_lost`, `not_started`), created per service provider. `CqrsTelemetry`
  publishes every source, meter, instrument and tag name (`AddSource(CqrsTelemetry.ActivitySourceNames)`,
  `AddMeter(CqrsTelemetry.MeterNames)`).
- **EF Core.** `EfCoreUnitOfWork<TContext>` (`UseEntityFrameworkCoreUnitOfWork<TContext>()`); hosted retention services
  that purge processed messages (`ProcessedRetention`, 7 days), dead letters (`DeadLetterRetention`, off), inbox
  records (`InboxRetention`, 7 days) and expired idempotency keys in bounded pages; the host fails to start when the
  context does not map the tables a registered store needs. The entity types work with lazy-loading and
  change-tracking proxies.
- **Redis.** A factory overload of every store verb (`UseRedis(sp => …)`) for a connection the container owns; the
  outbox and the idempotency store can use different servers.
- `[NotificationHandlerName]`, `NotificationNameAttribute.PartitionBy`, `IPartitionedNotification`, `IOutboxSignal`,
  `OutboxRetryOptions`, `OutboxProcessorOptions.MaxDegreeOfParallelism` / `Retry` / `UseInbox` /
  `BacklogSampleInterval` / `UnknownRecipientGracePeriod`, `UnitOfWorkOptions.RollbackOnFailedResult`,
  `BackgroundTaskQueueOptions.DrainOnShutdown`, `RequestContextBase(DateTime createdAt)`,
  `CqrsPipelinePriorities.Validation` / `ExceptionHandling`.
- Diagnostics: the analyzer `CQRA015` (`ITransactionalCommand` on a request that is not a command), `CQRGEN011` to
  `CQRGEN019` (partition key, handler names, fingerprints, `AddCqrsGenerated` under `InternalsVisibleTo`, request
  attributes and response types generated code cannot use, duplicate context factories), `CQRCONF009` to `CQRCONF012`
  (handler-name and notification-name clashes, hand-registered handlers of a durable notification, and ones that
  cannot be constructed), and the binding warnings `CQRDIAG005` / `CQRDIAG006` (validators or exception hooks whose
  behavior is not in the request's pipeline, across assemblies). See [docs/diagnostics.md](docs/diagnostics.md).
- Every package ships its XML documentation, tracks its public API (`PublicAPI.Shipped.txt`) and runs package
  validation on pack.

### Fixed

- **Outbox.** In `Enabled` mode, a notification published outside a transactional unit of work was buffered and then
  dropped; one published by a request that then failed stayed buffered, and a retried request stored it twice. The
  scoped buffer was not safe for the `Parallel` publish strategy. A handler's `JsonException` was dead-lettered as a
  corrupt payload without a retry. A handler's `OperationCanceledException` stopped the host and was redelivered
  forever. One handler's tracked `DbContext` state could be committed by the next message's handler.
- **Notifications.** An `INotificationHandler<INotification>` made the generated dispatcher call itself (stack
  overflow). The notification dispatcher cached "has a stable name" process-wide, shared by every host in the process.
- **Dispatch.** A throwing pre-handler produced no terminal lifecycle notification; a throwing `*Failed` subscriber
  replaced the handler's exception. An exception action for a base exception type was skipped once a handler for a
  derived type handled the exception. A second `AddCqrsGenerated` call registered behaviors again, so an idempotent
  request was rejected as its own duplicate. `RequestContextBase()` read the system clock instead of the application's
  `TimeProvider`.
- **Unit of work.** Rollback ran under the caller's (often already cancelled) token and could leave the transaction
  open for the rest of the scope; it runs under `CancellationToken.None`.
- **Resilience and timeouts.** A dependency's `OperationCanceledException` was treated as caller cancellation and never
  retried, and `DuplicateRequestException` was retried; a caller cancellation that raced the deadline was reported as a
  timeout.
- **Idempotency.** The EF Core store was a singleton holding the scoped `DbContext` (the host failed to start under
  scope validation). A claimant whose claim had expired could release its successor's claim (EF Core and Redis).
- **EF Core.** The outbox and idempotency tables grew without bound. A context configured with
  `UseQueryTrackingBehavior(NoTracking)` made the stores' writes save nothing. An EF Core store replaced by another
  registration no longer runs its retention or checks its context's model at host start.
- **Redis.** A connection passed to a store verb was ignored when the app registered its own `IConnectionMultiplexer`.
- **In-memory outbox.** Two processors could both claim a message whose lease had expired, and processed messages were
  never removed.
- **Generator.** Build breaks on legal code: `partial` handlers and requests, array result types (silently unrouted),
  keyword-named properties, a handler for a base notification type next to a derived one, a concrete request deriving
  from another, `required` members, disabled `ImplicitUsings`, and AOT hints that ignored generic constraints.
  Interceptor attributes lost named and `params` arguments; interceptors and `[PipelineExemption]` on a base request
  class were dropped. The outbox serializer ignored inherited properties and misread enums outside the `int` range. An
  assembly with only validators got no module, so its validators never ran. Generated code broke a `-warnaserror` build
  over an `[Obsolete]` handler, request or notification and failed to compile over an `[Experimental]` one; generated
  files now suppress those warnings for the types they name, and a type obsolete as an error is skipped with
  `CQRGEN006` / `CQRGEN010`.
- **Analyzers.** `CQRA005` reported an exemption on a base request class as dead although the generator applies it to
  derived requests; `CQRA008` offered a rewrite that would also exempt derived requests (it is now offered only on
  sealed requests and value types).
- **Options.** Values a timer cannot wait for (above about 24.8 days), negative durations and undefined enum values
  are rejected at host start instead of failing per request. The in-memory and EF Core store durations are capped at
  10 years and the EF Core `PurgeInterval` at the longest timer delay, so `TimeSpan.MaxValue` fails at start instead of
  overflowing the clock arithmetic.

### Known limitations

- Redis Cluster: the stores' scripts touch several keys, so every key prefix carries a hash tag (enforced at start).
- Native AOT: an open-generic behavior wraps a value-type-result request or a struct notification only when the
  application's generated code can name it; one it cannot is reported as `CQRDIAG004`.
- On .NET 8 and 9, ASP.NET Core's exception handler middleware logs every exception at `Error` before
  `CqrsExceptionHandler` maps it; .NET 10 does not.

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
  deliberate. A value-returning command dispatches through the query path and, being neither an `ICommand` nor an
  `IQuery<T>`, publishes no lifecycle notifications.

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
