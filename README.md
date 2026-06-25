# CQRSharp

[![NuGet version (CQRSharp)](https://img.shields.io/nuget/v/CQRSharp.svg?style=flat-square)](https://www.nuget.org/packages/CQRSharp/)
[![CodeQL](https://github.com/BisocM/CQRSharp/actions/workflows/github-code-scanning/codeql/badge.svg?branch=Release)](https://github.com/BisocM/CQRSharp/actions/workflows/github-code-scanning/codeql)
[![Qodana](https://github.com/BisocM/CQRSharp/actions/workflows/qodana_code_quality.yml/badge.svg)](https://github.com/BisocM/CQRSharp/actions/workflows/qodana_code_quality.yml)

![Alt](https://repobeats.axiom.co/api/embed/1d9c645b87f2a7c1c24211e12b02407a8df0ff87.svg "Repobeats Analytics")

A lightweight, extensible, and attribute-driven Command Query Responsibility Segregation (CQRS) framework for .NET
applications, with complete Native AOT support.

A Roslyn source generator wires up dispatching and registration at compile time, so there is no runtime reflection and
the whole framework is trimming- and Native-AOT-friendly.

For more information, please advise the [wiki](https://github.com/BisocM/CQRSharp/wiki) page!

---

## Features

- **Native AOT & trimming-safe** — a Roslyn source generator wires up all dispatch and registration at compile time; zero runtime reflection.
- **Attribute-driven** — define commands, queries, streaming requests, and notifications with simple marker interfaces and handlers.
- **One façade** — dispatch everything through `ICqrsDispatcher` (`Send` / `Stream` / `Publish`).
- **Build-time analyzers** — catch missing handlers, the wrong dispatch method, and mis-wired pipeline exemptions as you type (CQRA diagnostics), with code fixes.
- **Opt-in pipeline behaviors** — validation, resilience/retries, timeouts, rate limiting, idempotency, unit-of-work, and logging via `AddCqrsPipelinePack`.
- **Reliable messaging** — a background task queue and a transactional outbox with at-least-once delivery and distributed-tracing propagation.
- **Introspection** — a diagnostics API and health checks that describe exactly how each request is bound.

---

## Installation

```bash
dotnet add package CQRSharp
```

The `CQRSharp` meta-package pulls in the abstractions, core runtime, and the source generator for a plug-and-play setup.

## Quick Start

Register CQRSharp on your host. `AddCqrsGenerated` is emitted by the source generator and wires up every discovered
handler:

```csharp
using CQRSharp.Core.Extensions;

services.AddCqrsGenerated();
```

Define a command and its handler:

```csharp
using CQRSharp.Abstractions.Interfaces.Handlers;
using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Models.Commands;

public sealed class CreateUser : ICommand
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

Queries (`IQuery<TResult>` / `IQueryHandler<,>`), streaming requests (`IStreamRequest<TItem>` via `dispatcher.Stream`),
and notifications (`INotification` via `dispatcher.Publish`) follow the same pattern. Cross-cutting behaviors
(validation, rate limiting, timeout, resilience, unit-of-work) are opt-in via `AddCqrsPipelinePack`.

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
