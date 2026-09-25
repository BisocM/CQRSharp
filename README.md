# CQRSharp

[![NuGet version (CQRSharp)](https://img.shields.io/nuget/v/CQRSharp.svg?style=flat-square)](https://www.nuget.org/packages/CQRSharp/)
[![Build](https://github.com/BisocM/CQRSharp/actions/workflows/nuget_publish.yml/badge.svg?branch=Release)](https://github.com/BisocM/CQRSharp/actions/workflows/nuget_publish.yml)
[![CodeQL](https://github.com/BisocM/CQRSharp/actions/workflows/github-code-scanning/codeql/badge.svg?branch=Release)](https://github.com/BisocM/CQRSharp/actions/workflows/github-code-scanning/codeql)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg?style=flat-square)](LICENSE)

Commands, queries and events for .NET 8, 9 and 10, plus the plumbing you would otherwise write around them.

- **Wired at compile time.** A source generator finds your handlers, so there is no reflection or assembly scanning, it
  runs under Native AOT, and a request without a handler shows up in your IDE, not in production.
- **Faster than MediatR** inside a scope, for requests, streams and notifications ([benchmarks](benchmarks/README.md#latest-results)).
- **Batteries included, pay for what you use.** Validation, retries, timeouts, rate limiting, idempotency, a unit of work
  and a transactional outbox (Redis or EF Core) are one builder call each, and cost nothing where you do not use them.

MIT licensed. [Documentation](docs/README.md) · [Changelog](CHANGELOG.md) · [Project page](https://bisocm.org/projects/cqrsharp)

---

## Quick start

```bash
dotnet add package CQRSharp
```

```csharp
// 1. Register. AddCqrsGenerated is emitted by the source generator: it wires every handler it discovered, plus the
//    validation and exception-handling behaviors.
services.AddCqrsGenerated();

// 2. Define a request and its handler.
public sealed class CreateUser : CommandBase
{
    public required string Name { get; init; }
}

public sealed class CreateUserHandler : ICommandHandler<CreateUser>
{
    public Task<CommandResult> Handle(CreateUser command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult.FromSuccess());
}

// 3. Dispatch through ICqrsDispatcher. A failure is returned, not thrown: check IsSuccess.
public sealed class Users(ICqrsDispatcher dispatcher)
{
    public async Task<bool> Create(string name)
    {
        CommandResult result = await dispatcher.Send(new CreateUser { Name = name });
        return result.IsSuccess;
    }
}
```

The snippet needs no `using` lines: the `CQRSharp` meta-package adds global usings for its namespaces
([Getting started](docs/getting-started.md#global-usings) explains them
and the opt-out). Queries derive from `QueryBase<TResult>`, streams from `StreamRequestBase<TItem>` and are dispatched with
`dispatcher.Stream`, and notifications implement `INotification` and go through `dispatcher.Publish`. Scaffold a working
app with `dotnet new cqrsharp`, or read
[Getting started](docs/getting-started.md).

### Configuring the pipeline

Pass a builder to `AddCqrsGenerated` to add behaviors and stores. The order of the verbs does not matter: the builder
applies each behavior at its fixed place in the pipeline. In this form validation and exception handling are on (they do
nothing for a request without validators or exception hooks); every other behavior runs only when its verb is called.
Wiring mistakes do not fail silently: an `IIdempotentRequest` whose behavior was never enabled fails its dispatch, and the
startup validator, on by default in the Development environment and everywhere with `ValidateOnStart()`, reports every
such mistake at host start instead:

```csharp
services.AddCqrsGenerated(b => b
    .UseResilience(r => r.MaxRetries = 3)
    .UseEntityFrameworkCoreUnitOfWork<AppDbContext>()
    .UseIdempotency(i => i.UseRedis(redisConnectionString))
    .UseOutbox(o => o.UseEntityFrameworkCore<AppDbContext>())
    .ValidateOnStart());
```

### Packages

| Package | Adds |
| --- | --- |
| `CQRSharp` | The meta-package to install: the contracts, the runtime, the pipeline behaviors and builder, the source generator, and the global usings. |
| `CQRSharp.Abstractions` | The contracts alone (requests, notifications, results, store and unit-of-work interfaces) and the analyzers. A project that only declares requests can reference it; a project that declares handlers needs `CQRSharp`. |
| `CQRSharp.Core` | The runtime: the dispatcher, the pipeline executor, notification publishing, the background queue, the outbox processor, diagnostics and the in-memory stores. Pulled in by the meta-package. |
| `CQRSharp.Pipelines` | The built-in behaviors and the fluent builder. Pulled in by the meta-package. |
| `CQRSharp.Redis` | Redis outbox, inbox and idempotency stores. [Docs](docs/integrations.md) |
| `CQRSharp.EntityFrameworkCore` | EF Core (relational) outbox, inbox and idempotency stores, and `EfCoreUnitOfWork<TContext>`. [Docs](docs/integrations.md) |
| `CQRSharp.AspNetCore` | `CommandResult` → `IResult` (the status follows the result's error kind: 400 / 401 / 403 / 404 / 409 / 503), pipeline exceptions → ProblemDetails (400 / 409 / 422 / 429 / 503 / 504), `Idempotency-Key` header handling. [Docs](docs/aspnetcore.md) |
| `CQRSharp.FluentValidation` | Runs your FluentValidation validators inside the validation behavior. [Docs](docs/fluentvalidation.md) |
| `CQRSharp.Testing` | `RecordingCqrsDispatcher`, a stub-and-record dispatcher for unit tests; no test-framework dependency. [Docs](docs/testing-package.md) |
| `CQRSharp.Testing.Xunit.V3` | The store contract-test suites (for a custom outbox, inbox or idempotency store), on xUnit v3. [Docs](docs/testing-package.md) |
| `CQRSharp.Templates` | `dotnet new install CQRSharp.Templates`, then `dotnet new cqrsharp`. |

Which packages work under Native AOT: [Native AOT](docs/native-aot.md).

---

## What you get

- **Compile-time wiring.** Dispatch, registration and outbox serialization are generated code: no `MakeGenericType`, no
  assembly scanning, no reflection-based JSON. Multi-assembly solutions compose automatically: one `AddCqrsGenerated`
  wires the handlers of the calling assembly and of every assembly it references.
- **Analyzers with code fixes.** Missing or duplicate handlers, a stream request sent with `Send`, a pipeline exemption
  that has no effect, a custom request context without a factory, a direct `AddCqrs()` call: reported as you type
  (`CQRA*`, `CQRGEN*`). See [Diagnostics](docs/diagnostics.md).
- **A defined request lifecycle.** Pre- and post-handler interceptors, post-handlers that see the returned value *or* the
  exception, and `Initiated` → `Completed` / `Failed` lifecycle notifications: once a request is initiated, exactly one
  terminal notification follows. Commands, queries and streams follow the same contract.
- **Reliable messaging.** A transactional outbox with at-least-once delivery, persisted retry and back-off,
  dead-lettering and W3C trace propagation. Delivery is **per handler**: each handler of a notification gets its own
  stored message, attempts and dead letter, so a failing handler never makes a healthy one run again. Deliveries are
  **ordered per partition key** (`PartitionBy = nameof(OrderId)`), also across several processor instances, which claim
  messages under leases so they can share one outbox. An **inbox** records each delivery, so a redelivered message is
  recognized and skipped; with an EF Core inbox and unit of work over one `DbContext`, the handler's changes and the
  record commit together. Dead letters can be listed, requeued and purged. See
  [The outbox](docs/outbox.md).
- **Idempotency that answers the retry.** A duplicate of a completed request gets the **original result** back (a plain
  `CommandResult` always; any other result through a result serializer), a duplicate of one still running gets a
  distinguishable "in progress" (409 with `Retry-After` over HTTP), and a key reused with a **different payload** is
  rejected (422), not replayed.
- **Observability.** `ActivitySource` spans for every dispatch, behavior and outbox delivery; request-duration,
  notification, outbox and queue metrics; outbox backlog gauges and an outbox health check; a diagnostics API that
  reports how each request is bound; source-generated log messages with stable event ids. Nothing is measured until
  something listens.
- **Testability.** Every time read goes through `TimeProvider`, so retries, timeouts, leases and expiry are
  deterministic under `FakeTimeProvider`. `CQRSharp.Testing.Xunit.V3` ships the contract suites every built-in store
  passes (derive one class to check a custom store), and `CQRSharp.Testing` a recording dispatcher for unit-testing code
  that dispatches.

---

## CQRSharp and MediatR

[MediatR](https://github.com/LuckyPennySoftware/MediatR) is the ubiquitous runtime mediator; CQRSharp is a CQRS
*framework*: generated dispatch plus the infrastructure that usually gets hand-rolled around a mediator.

| | CQRSharp | MediatR |
| --- | --- | --- |
| License | MIT | Commercial since v13 (12.x and earlier remain Apache-2.0) |
| Handler discovery | Source generator | Runtime assembly scanning + reflection |
| Native AOT / trimming | Yes, built and run under AOT in CI | Not a design goal |
| Missing / duplicate handler | Build-time diagnostic | Runtime exception |
| Command / query distinction | First-class (`ICommand`, `IQuery<T>`, `CommandResult`) | One `IRequest<T>` |
| Pipeline behaviors | Yes, priority-ordered; per-request exemptions | Yes, registration-ordered |
| Streaming requests | Yes, with stream behaviors | Yes |
| Notifications | Sequential / parallel strategies, notification behaviors | Pluggable publisher |
| Built-in validation, retry, timeout, rate limiting | Yes | No, bring your own behaviors |
| Idempotency (in-memory / Redis / EF Core stores), unit of work | Yes, with result replay | No |
| Transactional outbox | Yes, in-memory / Redis / EF Core stores; multi-instance; per-handler delivery; ordered per key | No |
| ASP.NET Core result / ProblemDetails mapping, FluentValidation adapter, test doubles | Yes (separate packages) | No |
| Startup configuration validation | Yes | No |
| Tracing / metrics | Built in (`ActivitySource`, `Meter`) | No |

**Choose MediatR** if you want the de-facto standard and its ecosystem, and the licensing fits. **Choose CQRSharp** if
you want AOT-safe dispatch *and* the outbox, idempotency, resilience and diagnostics from one tested, MIT-licensed place.

### Dispatch overhead

Measured with BenchmarkDotNet on trivial handlers, with a new request object per call and every library in its documented
default configuration, so the numbers are each framework's own cost. Two things are measured: a dispatch through a
mediator resolved once from a long-lived DI scope, and a whole request scope (create a scope, resolve the mediator,
dispatch once, dispose the scope), which is what a web request pays. `ICqrsDispatcher` is scoped, so the per-scope numbers
include resolving it in every new scope. MediatR is benchmarked at 12.5.0, its last Apache-2.0 release. The method, the
scenarios and the commands to reproduce the run are in
[benchmarks/README.md](benchmarks/README.md).

The harness covers a request, a request with one behavior, a notification (published as its own type and as
`INotification`) and a three-item stream, each both ways. The results of the current harness, as the tables of one full
run with every library side by side, are published in
[benchmarks/README.md](benchmarks/README.md#latest-results); compare
the ratios there, since the absolute times move with the machine.

Three design points keep the per-dispatch cost low: lifecycle notifications and pipeline stages are **pay-for-use**
(nothing is resolved or published for a stage the container has no registration for, and what is registered is asked
once per provider and message type, not per dispatch); publishing is provider-wide, so a scope costs a publish nothing to
set up; and when nothing wraps a handler `Send` returns the handler's own task.

---

## Repository layout

```
src/          the packages: CQRSharp (meta), .Abstractions, .Core, .Pipelines, .Generators, .Analyzers,
              .Redis, .EntityFrameworkCore, .AspNetCore, .FluentValidation, .Testing, .Testing.Xunit.V3
samples/      CQRSharp.Sample (end-to-end self-test, Native AOT canary), CQRSharp.Sample.AspNetCore (minimal API
              over CQRSharp.AspNetCore, Native AOT canary for the HTTP edge), CQRSharp.Sample.ExternalModule
tests/        the test suite and a second-assembly fixture
benchmarks/   the BenchmarkDotNet comparison (not part of the solution)
templates/    the `dotnet new cqrsharp` template and the CQRSharp.Templates package project
docs/         the documentation
```

Build and test with `dotnet build CQRSharp.sln -warnaserror` and `dotnet test --project tests/CQRSharp.Tests`. The Redis,
PostgreSQL and SQL Server tests use the servers named by `CQRSHARP_TEST_REDIS`, `CQRSHARP_TEST_POSTGRES` and
`CQRSHARP_TEST_SQLSERVER` when set, and otherwise start containers with Testcontainers; without Docker they skip.
[CONTRIBUTING.md](CONTRIBUTING.md) has the details.

## Contributing

Issues and pull requests are welcome. CI builds every pull request with warnings as errors and runs the full suite; a
change to the generator should keep `IncrementalGeneratorCachingTests` green and come with a case in
`GeneratedCodeCompilesTests`. Releases are cut by bumping `<Version>` in `Directory.Build.props` on the `Release` branch.

## License

[MIT](LICENSE).
