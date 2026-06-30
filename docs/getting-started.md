# Getting started

This guide takes you from an empty project to dispatching commands, queries, streaming requests, and
notifications, then layering on opt-in behaviors and a transactional outbox.

- [Install](#install)
- [Register CQRSharp](#register-cqrsharp)
- [Global usings](#global-usings)
- [Your first command](#your-first-command)
- [Queries](#queries)
- [Streaming requests](#streaming-requests)
- [Notifications](#notifications)
- [Adding behaviors](#adding-behaviors)
- [Adding an outbox](#adding-an-outbox)
- [Where to go next](#where-to-go-next)

## Install

```bash
dotnet add package CQRSharp
```

The `CQRSharp` meta-package pulls in the abstractions, the runtime, the source generator, and the
analyzers. For durable outbox / idempotency persistence, also add an integration package:

```bash
dotnet add package CQRSharp.Redis                 # Redis-backed stores (Native-AOT-compatible)
dotnet add package CQRSharp.EntityFrameworkCore   # EF Core relational stores
```

CQRSharp targets **net8.0, net9.0, and net10.0**. The authoring contracts also compile against
`netstandard2.0` consumers.

## Register CQRSharp

`AddCqrsGenerated` is **emitted by the source generator** into your assembly. It registers every
command, query, stream, and notification handler the generator discovered, plus the dispatcher and the
AOT-safe registries — no assembly scanning, no reflection.

```csharp
// Minimal: just the discovered handlers and the dispatcher.
services.AddCqrsGenerated();

// Fluent: opt into behaviors and stores in any order (see Configuration).
services.AddCqrsGenerated(builder => builder
    .UseValidation()
    .UseLogging()
    .ValidateOnStart());
```

> **Always use `AddCqrsGenerated`, never `AddCqrs` directly.** `AddCqrsGenerated` applies the
> source-generated registrations; the startup validator will raise `CQRCONF004` at host start if it
> detects the generated registry is missing.

## Global usings

The framework's authoring types live in several namespaces. On a project with `ImplicitUsings`
enabled (the .NET 8+ default), the meta-package ships **global usings** so you don't have to import
them: `CommandBase`, `QueryBase<>`, `ICommandHandler<>`, `CommandResult`, `INotification`,
`ICqrsDispatcher`, `AddCqrsGenerated`, the builder verbs, and friends are already in scope.

Opt out — without disabling all implicit usings — with:

```xml
<PropertyGroup>
  <CQRSharpImplicitUsings>false</CQRSharpImplicitUsings>
</PropertyGroup>
```

The snippets in these docs assume the global usings are active.

## Your first command

A **command** expresses an intent to change state. Derive it from `CommandBase` and handle it with
`ICommandHandler<TCommand>`. A command's result is always a [`CommandResult`](requests-and-handlers.md#commandresult).

```csharp
public sealed class CreateUser : CommandBase
{
    public required string Name { get; init; }
}

public sealed class CreateUserHandler : ICommandHandler<CreateUser>
{
    public async Task<CommandResult> Handle(CreateUser command, CancellationToken ct)
    {
        // ... persist the user ...
        return CommandResult.FromSuccess();
        // or: return CommandResult.FromError("Name already taken", errorCode: 409);
    }
}
```

Dispatch it through the single façade, `ICqrsDispatcher`:

```csharp
public sealed class UsersController(ICqrsDispatcher cqrs)
{
    public Task<CommandResult> Create(string name)
        => cqrs.Send(new CreateUser { Name = name });
}
```

## Queries

A **query** returns data and changes nothing. Derive it from `QueryBase<TResult>` and handle it with
`IQueryHandler<TQuery, TResult>`. The same `Send` method returns the query's result type.

```csharp
public sealed class GetUserName : QueryBase<string>
{
    public required Guid Id { get; init; }
}

public sealed class GetUserNameHandler : IQueryHandler<GetUserName, string>
{
    public Task<string> Handle(GetUserName query, CancellationToken ct)
        => Task.FromResult("Ada");
}

// string name = await cqrs.Send(new GetUserName { Id = id });
```

## Streaming requests

A **streaming request** yields a sequence asynchronously. Derive it from `StreamRequestBase<TItem>`,
handle it with `IStreamRequestHandler<TRequest, TItem>`, and dispatch it with `Stream` (not `Send`).

```csharp
public sealed class TailLog : StreamRequestBase<string>;

public sealed class TailLogHandler : IStreamRequestHandler<TailLog, string>
{
    public async IAsyncEnumerable<string> Handle(TailLog request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(100, ct);
            yield return $"line {i}";
        }
    }
}

// await foreach (var line in cqrs.Stream(new TailLog())) { ... }
```

> Sending a stream request through `Send(...)` compiles but throws at runtime. The **CQRA004**
> analyzer flags this with a code fix that switches the call to `Stream(...)`.

## Notifications

A **notification** is a one-to-many event. Implement `INotification`, handle it with one or more
`INotificationHandler<TNotification>`, and publish it with `Publish`.

```csharp
public sealed record UserCreated(Guid Id) : INotification;

public sealed class SendWelcomeEmail : INotificationHandler<UserCreated>
{
    public Task Handle(UserCreated n, CancellationToken ct) => /* ... */ Task.CompletedTask;
}

// await cqrs.Publish(new UserCreated(id));
```

By default, handlers run **sequentially** (the safe choice — they share the dispatching DI scope). See
[Notifications](notifications.md) for publish strategies and the durable-via-outbox path.

## Adding behaviors

Cross-cutting behaviors are opt-in and composed through the fluent builder. Order never matters — the
builder applies them in a fixed canonical sequence.

```csharp
services.AddCqrsGenerated(builder => builder
    .UseLogging()
    .UseValidation()
    .UseExceptionHandling()
    .UseTimeout(o => o.Timeout = TimeSpan.FromSeconds(30))
    .UseResilience(o => o.MaxRetries = 3)
    .UseRateLimiting(o => { o.MaxTokens = 100; o.ReplenishRatePerSecond = 10; o.MaxEntries = 10_000; }));
```

See [Pipeline behaviors](pipeline-behaviors.md) for what each one does and how to write your own.

## Adding an outbox

The outbox is **off by default**. Enable it in one cohesive step that selects the mode and registers a
store:

```csharp
services.AddCqrsGenerated(builder => builder
    .UseOutbox(o => o.Transactional().UseInMemoryStore()));   // dev/test
```

For durable persistence, swap the store verb for `o.UseRedis(...)` or
`o.UseEntityFrameworkCore<MyDbContext>()`. A notification is only carried by the outbox if it is marked
with `[NotificationName("...")]`. See [The outbox](outbox.md) for the full story.

## Where to go next

- [Requests and handlers](requests-and-handlers.md) — the full request/handler/context model.
- [Configuration](configuration.md) — every builder verb and option.
- [Pipeline behaviors](pipeline-behaviors.md) — built-ins and custom behaviors.
- [Diagnostics & validation](diagnostics.md) — the analyzers and startup checks that keep you honest.
