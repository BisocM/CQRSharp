# CQRSharp documentation

These pages describe **CQRSharp 5.0**: a CQRS framework for .NET 8, 9 and 10 whose dispatch and registration a Roslyn
source generator writes at compile time. You declare commands, queries, streaming requests and notifications with their
handlers, and dispatch them through one façade, `ICqrsDispatcher`. What the project offers, the package list and a quick
start are in the [repository README](../README.md); the namespaces are mapped [below](#namespaces).

## Table of contents

**Start here**
- [Getting started](getting-started.md): install, a first program, and a first command, query, stream and notification,
  then behaviors and an outbox.

**Core concepts**
- [Requests and handlers](requests-and-handlers.md): the request kinds and base classes, handlers, `ICqrsDispatcher`,
  `CommandResult`, value-returning commands, and the request context.
- [Notifications](notifications.md): publishing, which handlers a notification reaches, publish strategies, lifecycle
  notifications, and notification behaviors.

**Configuration and the pipeline**
- [Configuration](configuration.md): `AddCqrsGenerated` and every builder verb, dispatcher, notification and queue
  options, hosting and lifetimes, startup validation, and the `TimeProvider` seam.
- [Pipeline behaviors](pipeline-behaviors.md): the pipeline model and its order, the built-in behaviors, validation,
  exception hooks, custom behaviors, exemptions, and pre- and post-handler interceptors.

**Reliability**
- [The outbox](outbox.md): outbox modes, durable notifications, per-handler delivery, ordering, the inbox, the processor,
  dead letters, and stores.
- [Idempotency and resilience](idempotency-and-resilience.md): idempotent requests and their stores, result replay,
  retries, and timeouts.
- [Unit of work and transactions](unit-of-work.md): `IUnitOfWork`, transactional commands and queries, isolation levels,
  and how the outbox takes part in a transaction.

**Integrations**
- [Redis and EF Core stores](integrations.md): the `CQRSharp.Redis` and `CQRSharp.EntityFrameworkCore` stores and the EF
  Core unit of work.
- [ASP.NET Core](aspnetcore.md): `CommandResult` to `IResult`, exceptions to ProblemDetails, and the `Idempotency-Key`
  header.
- [FluentValidation](fluentvalidation.md): running FluentValidation validators inside the validation behavior.

**Tooling and operations**
- [Diagnostics and validation](diagnostics.md): the `CQRA` analyzers, the `CQRGEN` generator diagnostics, the `CQRCONF`
  startup checks, the introspection API, and the outbox health check.
- [Observability](observability.md): tracing, metrics, and log event ids.
- [Testing](testing.md): testing handlers, the dispatcher and time-dependent behavior.
- [The testing packages](testing-package.md): `RecordingCqrsDispatcher` and the store contract suites.

**Platform and internals**
- [Native AOT](native-aot.md): what is AOT-safe, publishing, and the limits for value-type results.
- [The source generator](source-generator.md): what it emits, how it recognizes framework types, incrementality, and
  multi-assembly applications.

## Namespaces

The namespaces follow who writes the code:

- **`CQRSharp`**: everyday application code. Requests and their base classes, handler interfaces, `CommandResult` and
  `ValidationFailure`, notifications (yours and the lifecycle ones), the request context and its factory, interceptors,
  validators, exception hooks, the request markers (`IIdempotentRequest`, `IRetryableRequest`, `ITransactionalCommand`,
  ...), `ICqrsDispatcher`, `AddCqrsGenerated`, the option types (`DispatcherOptions`, `NotificationOptions`,
  `OutboxOptions`, ...), and the exceptions callers catch (`RequestValidationException`, `DuplicateRequestException`,
  `IdempotencyKeyMismatchException`, `RateLimitExceededException`, `RequestTimeoutException`,
  `BackgroundTaskRejectedException`).
- **`CQRSharp.Pipelines`**: pipeline authoring and configuration. The fluent builder (`ICqrsBuilder`,
  `OutboxStoreBuilder`, `IdempotencyStoreBuilder`), the behavior contracts (`IPipelineBehavior<,>`,
  `IStreamPipelineBehavior<,>`, `INotificationPipelineBehavior<>`, `IPrioritizedPipelineBehavior`) and
  `CqrsPipelinePriorities`, the built-in behaviors (`LoggingBehavior<,>`, `RateLimitingBehavior<,>`, ..., the types
  `[PipelineExemption(typeof(...))]` names) and their options, and `IRateLimitedContext`.
- **`CQRSharp.Persistence`**: the contracts infrastructure implements. `IUnitOfWork`; the outbox and inbox stores
  (`IOutboxStore`, `IInboxStore`, `OutboxMessage`, `ClaimedOutboxMessage`, `OutboxClaim`, `OutboxBacklog`); the
  idempotency store (`IIdempotencyStore`, `IdempotencyClaim`, `IIdempotencyResultSerializer`); and
  `INotificationSerializer`.

With the `CQRSharp` meta-package and `ImplicitUsings`, `CQRSharp` and `CQRSharp.Pipelines` are global usings
([Global usings](getting-started.md#global-usings)); a file that implements a store or a unit of work adds
`using CQRSharp.Persistence;`.

Each integration package has one namespace named after it: `CQRSharp.Redis`, `CQRSharp.EntityFrameworkCore`,
`CQRSharp.AspNetCore`, `CQRSharp.FluentValidation` and `CQRSharp.Testing`. `CQRSharp.Testing.Xunit.V3` shares the
`CQRSharp.Testing` namespace. Their registration extensions (`AddRedisOutboxStore`, `UseRedis`,
`UseEntityFrameworkCore<TContext>`, `AddCqrsProblemDetails`, `UseFluentValidation`, ...) live in
`Microsoft.Extensions.DependencyInjection`, so they need no `using`. The EF Core model-builder extensions
(`ApplyCqrsOutbox`, `ApplyCqrsIdempotency`) live in `CQRSharp.EntityFrameworkCore`.

Runtime extension points live in one namespace per feature under `CQRSharp.Core`. You need them only to extend or observe
the framework itself:

| Namespace | Holds |
| --- | --- |
| `CQRSharp.Core.BackgroundTasks` | `IBackgroundTaskManager`, the queue behind `RunMode.Queued`. |
| `CQRSharp.Core.Diagnostics` | `ICqrsDiagnostics` and its binding records, and `CqrsTelemetry` (activity source, meter, instrument and tag names). |
| `CQRSharp.Core.Diagnostics.HealthChecks` | `AddCqrsOutbox`, the outbox health check, and `OutboxHealthCheckOptions`. Add `using CQRSharp.Core.Diagnostics.HealthChecks;` to call `AddCqrsOutbox`. |
| `CQRSharp.Core.Exceptions` | The exception-hook registry the exception-handling behavior reads. |
| `CQRSharp.Core.Idempotency` | `IRequestFingerprinter`, which the idempotency behaviors use to fingerprint a request's payload. |
| `CQRSharp.Core.Modules` | `ICqrsModule` and `CqrsModuleComposition`, through which generated modules are composed, and `DiscoveredServices`. |
| `CQRSharp.Core.Notifications` | `NotificationRoute` and `NotificationSubscription`, the notification tables generated modules fill. Publishing goes through `ICqrsDispatcher.Publish`. |
| `CQRSharp.Core.Outbox` | `IOutboxSignal`, which wakes the outbox processor, and `OutboxPartitionKey`. |
| `CQRSharp.Core.Pipelines` | `RequestRoute` and `StreamRoute`, the route tables generated modules fill, and the closed-behavior catalog used under Native AOT. |
| `CQRSharp.Core.Registries` | `RequestMetadata` and the registration helpers generated code calls. |
| `CQRSharp.Core.SourceGeneration` | The assembly-level marker attributes the generator writes and the analyzers read. |

Types in these namespaces that exist only for generated code are hidden from IntelliSense.
