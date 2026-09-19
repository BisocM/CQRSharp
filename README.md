# CQRSharp

[![NuGet version (CQRSharp)](https://img.shields.io/nuget/v/CQRSharp.svg?style=flat-square)](https://www.nuget.org/packages/CQRSharp/)
[![CI](https://github.com/BisocM/CQRSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/BisocM/CQRSharp/actions/workflows/ci.yml)
[![CodeQL](https://github.com/BisocM/CQRSharp/actions/workflows/github-code-scanning/codeql/badge.svg?branch=Release)](https://github.com/BisocM/CQRSharp/actions/workflows/github-code-scanning/codeql)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg?style=flat-square)](LICENSE)

A CQRS framework for .NET 8 / 9 / 10 that is wired at **compile time**. A Roslyn source generator emits the dispatch
tables, handler registrations and notification serializers as plain C#, so there is no runtime reflection, the whole
thing is trimming- and **Native-AOT-safe**, and a missing handler is a build warning instead of a production exception.

Beyond dispatch it ships the parts a CQRS codebase ends up needing anyway — validation, retries, timeouts, rate
limiting, idempotency, unit of work, and a **transactional outbox** with Redis and EF Core stores — all opt-in, all behind
one fluent builder.

MIT licensed. [Documentation](docs/README.md) · [Changelog](CHANGELOG.md) · [Project page](https://bisocm.org/projects/cqrsharp)

---

## Quick start

```bash
dotnet add package CQRSharp
```

```csharp
// 1. Register. AddCqrsGenerated is emitted by the source generator and wires every handler it discovered.
services.AddCqrsGenerated();

// 2. Define a request and its handler.
public sealed class CreateUser : CommandBase
{
    public required string Name { get; init; }
}

public sealed class CreateUserHandler : ICommandHandler<CreateUser>
{
    public Task<CommandResult> Handle(CreateUser command, CancellationToken ct)
        => Task.FromResult(CommandResult.FromSuccess());
}

// 3. Dispatch through the single façade.
public sealed class UsersController(ICqrsDispatcher dispatcher)
{
    public Task Create(string name) => dispatcher.Send(new CreateUser { Name = name });
}
```

The `CQRSharp` meta-package brings the abstractions, the runtime, the generator and the analyzers, and — with
`ImplicitUsings` on — the global usings that make the snippet above compile with no `using` lines (opt out with
`<CQRSharpImplicitUsings>false</CQRSharpImplicitUsings>`). Queries derive from `QueryBase<TResult>`, commands that return
a value from `ResultCommandBase<TResult>`, streams are dispatched with `dispatcher.Stream`, and notifications implement
`INotification` and go through `dispatcher.Publish`. Scaffold a working app with `dotnet new cqrsharp`, or read
[Getting started](docs/getting-started.md).

### Opting into behaviors

Nothing cross-cutting runs until you ask for it. The builder applies behaviors in the right order regardless of how you
list them, and a startup validator turns mis-wiring (an outbox with no store, an `IIdempotentRequest` whose behavior was
never enabled) into an error at host start rather than at first request:

```csharp
services.AddCqrsGenerated(b => b
    .UseValidation()
    .UseResilience(r => r.MaxRetries = 3)
    .UseUnitOfWork(sp => sp.GetRequiredService<AppUnitOfWork>())
    .UseIdempotency(i => i.UseRedis(connectionString))
    .UseOutbox(o => o.Transactional().UseEntityFrameworkCore<AppDbContext>())
    .ValidateOnStart());
```

| Package | Adds |
| --- | --- |
| `CQRSharp` | Meta-package: abstractions, runtime, source generator, analyzers, global usings. |
| `CQRSharp.Pipelines` | The opt-in behaviors and the fluent builder (pulled in by the meta-package). |
| `CQRSharp.Redis` | Redis outbox + idempotency stores. Native-AOT-compatible. |
| `CQRSharp.EntityFrameworkCore` | EF Core (relational) outbox + idempotency stores; the outbox joins your `DbContext` transaction. |

---

## What you get

- **Compile-time wiring.** Dispatch, registration and outbox serialization are generated code — no `MakeGenericType`, no
  assembly scanning, no reflection-based JSON. Multi-assembly solutions compose automatically: one `AddCqrsGenerated`
  wires every referenced assembly's handlers.
- **Analyzers with code fixes.** Missing or duplicate handlers, the wrong dispatch method for a request, a handler whose
  context type doesn't match its request, a marker interface whose behavior was never enabled — reported as you type
  (`CQRA*`, `CQRGEN*`), not discovered at runtime. See [Diagnostics](docs/diagnostics.md).
- **A defined request lifecycle.** Pre/post-handler interceptors, outcome-aware post-handlers that see the returned
  value *or* the exception, and `Initiated` → `Completed` / `Failed` lifecycle notifications with a guaranteed terminal
  event. Commands, queries and streams follow the same contract.
- **Reliable messaging.** A transactional outbox with at-least-once delivery, persisted retry/back-off, dead-lettering
  and W3C trace propagation; nothing published is silently dropped, and nothing from a failed request is delivered.
  Plus a bounded background task queue with a real graceful-shutdown window.
- **Observability.** `ActivitySource` spans for every dispatch, behavior and outbox delivery, queue metrics, health
  checks, and a diagnostics API that reports exactly how each request is bound.
- **Testability.** Every time read goes through `TimeProvider`, so retries, timeouts, leases and expiry are
  deterministic under `FakeTimeProvider`. The store contract-test suites the built-in stores pass live in
  `tests/CQRSharp.Testing.Outbox`, ready to run against a custom store.

---

## CQRSharp, MediatR and Mediator

Three libraries, three different bets. [MediatR](https://github.com/LuckyPennySoftware/MediatR) is the ubiquitous
runtime mediator; [Mediator](https://github.com/martinothamar/Mediator) is a source-generated, allocation-focused
reimplementation of that same mediator surface; CQRSharp is a CQRS *framework* — generated dispatch plus the
infrastructure that usually gets hand-rolled around a mediator.

| | CQRSharp | MediatR | Mediator |
| --- | --- | --- | --- |
| License | MIT | Commercial since v13 (12.x and earlier remain Apache-2.0) | MIT |
| Handler discovery | Source generator | Runtime assembly scanning + reflection | Source generator |
| Native AOT / trimming | Yes — built and run under AOT in CI | Not a design goal | Yes |
| Missing / duplicate handler | Build-time diagnostic | Runtime exception | Build-time diagnostic |
| Command / query distinction | First-class (`ICommand`, `IQuery<T>`, `CommandResult`) | One `IRequest<T>` | `ICommand<T>` / `IQuery<T>` / `IRequest<T>` |
| Pipeline behaviors | Yes, priority-ordered; per-request exemptions | Yes, registration-ordered | Yes, registration-ordered |
| Streaming requests | Yes, with stream behaviors | Yes | Yes |
| Notifications | Sequential / parallel strategies, notification behaviors | Pluggable publisher | Pluggable publisher |
| Built-in validation, retry, timeout, rate limiting | Yes (opt-in) | No — bring your own behaviors | No — bring your own behaviors |
| Idempotency (in-memory / Redis / EF Core stores), unit of work | Yes (opt-in) | No | No |
| Transactional outbox | Yes, in-memory / Redis / EF Core stores | No | No |
| Startup configuration validation | Yes | No | No |
| Tracing / metrics | Built in (`ActivitySource`, `Meter`) | No | No |

**Choose MediatR** if you want the de-facto standard and its ecosystem, and the licensing fits. **Choose Mediator** if
you want the thinnest, fastest possible in-process mediator and will build the rest yourself. **Choose CQRSharp** if you
want AOT-safe dispatch *and* the outbox, idempotency, resilience and diagnostics to come from one tested, MIT-licensed
place.

### Dispatch overhead

Measured with BenchmarkDotNet — trivial handlers, one long-lived DI scope, a fresh request object per call, so the
number is each framework's own cost per dispatch ([source](benchmarks/CQRSharp.Benchmarks), reproduce with
`dotnet run -c Release --project benchmarks/CQRSharp.Benchmarks`). MediatR is benchmarked at 12.5.0, its last
Apache-2.0 release.

| Scenario | Library | Mean | vs MediatR | Allocated |
| --- | --- | ---: | ---: | ---: |
| Request | CQRSharp | 74.24 ns | 1.02× | 152 B |
| Request | MediatR 12.5 | 72.69 ns | 1.00× | 336 B |
| Request | Mediator 3.0 (source-gen) | 57.01 ns | 0.78× | 88 B |
| Request + 1 behavior | CQRSharp | 112.87 ns | 1.09× | 328 B |
| Request + 1 behavior | MediatR 12.5 | 103.25 ns | 1.00× | 528 B |
| Request + 1 behavior | Mediator 3.0 (source-gen) | 77.58 ns | 0.75× | 184 B |
| Notification | CQRSharp | 60.52 ns | 0.79× | 80 B |
| Notification | MediatR 12.5 | 76.33 ns | 1.00× | 312 B |
| Notification | Mediator 3.0 (source-gen) | 41.78 ns | 0.55× | 24 B |
| Request in a new DI scope | CQRSharp | 255.45 ns | 1.87× | 720 B |
| Request in a new DI scope | MediatR 12.5 | 136.93 ns | 1.00× | 568 B |
| Request in a new DI scope | Mediator 3.0 (source-gen) | 169.52 ns | 1.24× | 536 B |

<sub>BenchmarkDotNet v0.15.8, Windows 11 (10.0.22631.5039/23H2/2023Update/SunValley3); AMD Ryzen 9 7950X3D 4.20GHz; .NET 8.0.26</sub>

How to read it: **in-scope dispatch is on par with MediatR** (and publishes notifications faster), while allocating
about half as much; **Mediator is the fastest of the three** and the one to pick if raw in-process throughput is the goal.
The last row is what a web request actually pays — a fresh DI scope, the dispatcher resolved from it, one dispatch — and
there CQRSharp is the slowest: `ICqrsDispatcher` is deliberately *scoped* (so a singleton cannot capture one and
dispatch from the root provider), and each dispatch also creates a request context. It is ~120 ns, next to a handler
that does any I/O it is noise, but it is the honest number.

Two design points make the in-scope numbers possible without giving anything up: lifecycle notifications and pipeline
stages are **pay-for-use** (nothing is resolved or published for a stage the container has no registration for), and
when nothing brackets a handler `Send` returns the handler's own task.

---

## Repository layout

```
src/          the packages: CQRSharp (meta), .Abstractions, .Core, .Pipelines, .Generators, .Analyzers,
              .Redis, .EntityFrameworkCore
samples/      CQRSharp.Sample (full self-test, the Native AOT canary), .Sample.Minimal, .Sample.ExternalModule
tests/        the test suite, the shared store contract tests, a second-assembly fixture
benchmarks/   BenchmarkDotNet comparison (not part of the solution)
templates/    the `dotnet new cqrsharp` template
docs/         the documentation
```

Build and test with `dotnet build CQRSharp.sln -warnaserror` and `dotnet test tests/CQRSharp.Tests`. The Redis contract
tests run when a server is reachable on `localhost:6379` and skip cleanly otherwise.

## Contributing

Issues and pull requests are welcome. CI builds every pull request with warnings as errors and runs the full suite; a
change to the generator should keep `IncrementalGeneratorCachingTests` green and come with a case in
`GeneratedCodeCompilesTests`. Releases are cut by bumping `<Version>` in `Directory.Build.props` on the `Release` branch.

## License

[MIT](LICENSE).
