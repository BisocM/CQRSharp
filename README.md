# CQRSharp

[![NuGet version (CQRSharp)](https://img.shields.io/nuget/v/CQRSharp.svg?style=flat-square)](https://www.nuget.org/packages/CQRSharp/)
[![CodeQL](https://github.com/BisocM/CQRSharp/actions/workflows/github-code-scanning/codeql/badge.svg?branch=Release)](https://github.com/BisocM/CQRSharp/actions/workflows/github-code-scanning/codeql)

![Alt](https://repobeats.axiom.co/api/embed/1d9c645b87f2a7c1c24211e12b02407a8df0ff87.svg "Repobeats Analytics")

A lightweight, extensible, and attribute-driven Command Query Responsibility Segregation (CQRS) framework for .NET
applications, with complete Native AOT support.

A Roslyn source generator wires up dispatching and registration at compile time, so there is no runtime reflection and
the whole framework is trimming- and Native-AOT-friendly.

For more information, see the [CQRSharp project page](https://bisocm.org/projects/cqrsharp).

---

## Features

- **Native AOT & trimming-safe** — a Roslyn source generator wires up all dispatch and registration at compile time; zero runtime reflection.
- **Attribute-driven** — define commands, queries, streaming requests, and notifications with simple marker interfaces and handlers.
- **One façade** — dispatch everything through `ICqrsDispatcher` (`Send` / `Stream` / `Publish`).
- **Build-time analyzers** — catch missing handlers, the wrong dispatch method, and mis-wired pipeline exemptions as you type (CQRA diagnostics), with code fixes.
- **Opt-in pipeline behaviors** — validation, resilience/retries, timeouts, rate limiting, idempotency, unit-of-work, and logging via `AddCqrsPipelinePack`, composed order-insensitively through the fluent `AddCqrsGenerated(b => ...)` builder.
- **Reliable messaging** — a background task queue and a transactional outbox with at-least-once delivery and distributed-tracing propagation.
- **Batteries-included persistence** — drop-in in-memory, Redis (`CQRSharp.Redis`), and EF Core (`CQRSharp.EntityFrameworkCore`) stores for both the outbox and request idempotency; no hand-written atomic-claim code required.
- **Fail-fast configuration** — a startup validator surfaces silent mis-wiring (an outbox with no store, a non-transactional unit of work, outbox-bypassing notifications) as loud errors at host start instead of at first request.
- **Introspection** — a diagnostics API and health checks that describe exactly how each request is bound.

---

## Installation

```bash
dotnet add package CQRSharp
```

The `CQRSharp` meta-package pulls in the abstractions, core runtime, and the source generator for a plug-and-play setup.
On projects with `ImplicitUsings` enabled (the .NET 8+ default) it also ships **global usings** for the authoring
surface, so the snippets below need no `using` directives — `CommandBase`, `ICommandHandler<>`, `CommandResult`,
`ICqrsDispatcher`, `AddCqrsGenerated`, and friends are already in scope. (Opt out with
`<CQRSharpImplicitUsings>false</CQRSharpImplicitUsings>`.)

For durable outbox and idempotency persistence, add the integration package for your backing store (the core package
ships in-memory stores for development and single-node use out of the box):

```bash
dotnet add package CQRSharp.Redis                 # Redis-backed outbox + idempotency stores (Native-AOT-compatible)
dotnet add package CQRSharp.EntityFrameworkCore   # EF Core (relational) outbox + idempotency stores
```

## Quick Start

Register CQRSharp on your host. `AddCqrsGenerated` is emitted by the source generator and wires up every discovered
handler:

```csharp
services.AddCqrsGenerated();
```

Define a command and its handler:

```csharp
public sealed class CreateUser : CommandBase
{
    public required string Name { get; init; }
}

public sealed class CreateUserHandler : ICommandHandler<CreateUser>
{
    public Task<CommandResult> Handle(CreateUser command, CancellationToken ct)
        => Task.FromResult(CommandResult.FromSuccess());
}
```

Dispatch it through the single injected façade, `ICqrsDispatcher`:

```csharp
public sealed class UsersController(ICqrsDispatcher dispatcher)
{
    public Task Create(string name)
        => dispatcher.Send(new CreateUser { Name = name });
}
```

Queries derive from `QueryBase<TResult>` and are handled by `IQueryHandler<TQuery, TResult>`; streaming requests are
dispatched with `dispatcher.Stream`, and notifications implement `INotification` and are published with
`dispatcher.Publish`. Use `CommandBase<TContext>` / `QueryBase<TResult, TContext>` when a request needs a custom
context.

### Order-insensitive wiring

Cross-cutting behaviors and stores are opt-in and can be composed in any order through the fluent overload of
`AddCqrsGenerated`. The builder applies them in the correct sequence, so registration order never matters and a missing
store is reported at startup rather than at first request:

```csharp
services.AddCqrsGenerated(builder => builder
    .UseValidation()
    .UseInMemoryOutbox()   // dev/test; swap for AddRedisOutboxStore(...) or AddEntityFrameworkCoreOutboxStore<TContext>(...)
    .ValidateOnStart());
```

For durable persistence, register a store from an integration package — `AddRedisOutboxStore` /
`AddRedisIdempotencyStore`, or `AddEntityFrameworkCoreOutboxStore<TContext>` /
`AddEntityFrameworkCoreIdempotencyStore<TContext>`.

---

## Contributing

Contributions are welcome! Please open issues and pull requests for bug fixes, enhancements, or new features.

To contribute:

1. Fork the repository.
2. Create a new branch.
3. Make your changes.
4. Submit a pull request.

Please ensure that your code follows the project's coding standards and includes appropriate tests.

---

## License

This project is licensed under the [MIT License](LICENSE).

---


**Note**: This project is in active development.
