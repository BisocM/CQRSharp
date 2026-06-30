# CQRSharp Documentation

CQRSharp is a lightweight, **Native-AOT-first** CQRS (Command Query Responsibility Segregation)
framework for .NET 8/9/10. A Roslyn **source generator** wires up all dispatch and registration at
compile time, so there is **zero runtime reflection** and the whole framework is trimming- and
AOT-friendly. You author commands, queries, streaming requests, and notifications with small marker
interfaces and handlers, and dispatch everything through a single façade — `ICqrsDispatcher`.

> **Version:** these docs describe **CQRSharp 4.0**.

---

## Why CQRSharp

- **Native AOT & trimming-safe.** The source generator emits the dispatch tables, handler registries,
  and notification serializers as plain C#. No `MakeGenericType`, no reflection-based handler lookup,
  no `JsonSerializer` reflection. The libraries are marked `IsAotCompatible` and build under the AOT
  analyzers with warnings-as-errors.
- **Compile-time correctness.** Analyzers catch missing handlers, the wrong dispatch method, mis-wired
  pipeline exemptions, and markers that silently do nothing — as you type, with code fixes.
- **One façade.** Inject `ICqrsDispatcher` and call `Send` / `Stream` / `Publish`. That's the whole
  dispatch surface.
- **Fail-fast configuration.** A startup validator turns silent mis-wiring (an enabled outbox with no
  store, an idempotency marker with no behavior, …) into loud errors at host start.
- **Batteries included, opt-in.** Validation, logging, rate limiting, resilience/retries, timeouts,
  unit-of-work, idempotency, and a transactional outbox are all available through one order-insensitive
  fluent builder — and all off until you ask for them.

---

## Packages

| Package | Purpose |
| --- | --- |
| **`CQRSharp`** | Meta-package. Pulls in the abstractions, runtime, source generator, and analyzers for a plug-and-play setup. Ships convenience global usings. |
| `CQRSharp.Abstractions` | Contracts only: markers, handler interfaces, attributes, `CommandResult`, store/UoW abstractions. Reference this to define handlers without the runtime. |
| `CQRSharp.Core` | The runtime: `ICqrsDispatcher`, pipeline execution, notification publishing, the background queue and outbox processor, diagnostics, health checks, and the in-memory stores. |
| `CQRSharp.Pipelines` | Opt-in pipeline behaviors and the fluent builder (`UseValidation()`, `UseResilience(...)`, `UseOutbox(...)`, …). |
| `CQRSharp.Redis` | Durable Redis-backed outbox and idempotency stores (Native-AOT-clean). |
| `CQRSharp.EntityFrameworkCore` | Durable EF Core (relational) outbox and idempotency stores. *(Not AOT-compatible — EF Core uses runtime query compilation.)* |

```bash
dotnet add package CQRSharp
```

---

## Table of contents

**Getting started**
- [Getting started](getting-started.md) — install, your first command, query, notification, and behaviors.

**Core concepts**
- [Requests and handlers](requests-and-handlers.md) — commands, queries, streaming requests, the dispatcher, request context, `CommandResult`, and `RequestMetadata`.
- [Notifications](notifications.md) — `INotification`, fan-out, publish strategies, lifecycle notifications, and the notification pipeline.

**Configuration & the pipeline**
- [Configuration](configuration.md) — `AddCqrsGenerated`, the fluent builder, `DispatcherOptions` (run mode / scope mode), `NotificationOptions`, the `TimeProvider` seam, and startup validation.
- [Pipeline behaviors](pipeline-behaviors.md) — the pipeline model, execution order, the built-in behaviors, validation, custom behaviors, `[PipelineExemption]`, and exception hooks / interceptors.

**Reliability**
- [The outbox](outbox.md) — the transactional outbox, outbox modes, stores, the processor, and the `[NotificationName]` durability contract.
- [Idempotency & resilience](idempotency-and-resilience.md) — `IIdempotentRequest`, idempotency stores, `IRetryableRequest`, retries, and timeouts.
- [Unit of work & transactions](unit-of-work.md) — `IUnitOfWork` / `IExplicitUnitOfWork`, `ITransactionalCommand` / `ITransactionalQuery`, isolation levels, and outbox integration.

**Tooling, diagnostics & operations**
- [Diagnostics & validation](diagnostics.md) — the `CQRA` analyzers, `CQRGEN` generator diagnostics, `CQRCONF` startup validation, the diagnostics introspection API, and health checks.
- [Observability](observability.md) — distributed tracing and queue metrics.
- [Testing](testing.md) — store contract tests and testing your handlers.

**Platform & internals**
- [Native AOT](native-aot.md) — the AOT story, what is and isn't AOT-safe, and how to verify.
- [Integrations](integrations.md) — Redis and EF Core stores.
- [The source generator](source-generator.md) — what it emits, the well-known-type handoff, and AOT hints.

---

## CQRSharp in 30 seconds

```csharp
// Program.cs — register everything the generator discovered, plus opt-in validation.
services.AddCqrsGenerated(b => b.UseValidation());

// A command and its handler.
public sealed class CreateUser : CommandBase
{
    public required string Name { get; init; }
}

public sealed class CreateUserHandler : ICommandHandler<CreateUser>
{
    public Task<CommandResult> Handle(CreateUser command, CancellationToken ct)
        => Task.FromResult(CommandResult.FromSuccess());
}

// Dispatch through the single façade.
public sealed class Users(ICqrsDispatcher cqrs)
{
    public Task<CommandResult> Create(string name) => cqrs.Send(new CreateUser { Name = name });
}
```

No `using` directives are needed for the authoring types above: the `CQRSharp` meta-package ships
global usings on projects with `ImplicitUsings` enabled. See [Getting started](getting-started.md) for
details and how to opt out.
