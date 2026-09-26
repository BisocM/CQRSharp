# Getting started

This guide goes from an empty project to dispatching commands, queries, streaming requests and notifications, then adds
behaviors and an outbox.

- [Install](#install)
- [Your first app](#your-first-app)
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

The `CQRSharp` meta-package brings the contracts, the runtime, the pipeline behaviors and their builder, the source
generator, and the analyzers. It does not bring the .NET Generic Host: a program that calls `Host.CreateApplicationBuilder`
(like the one below) also needs

```bash
dotnet add package Microsoft.Extensions.Hosting
```

ASP.NET Core and Worker Service projects already have it. For durable outbox and idempotency stores, add an integration
package as well ([Integrations](integrations.md)):

```bash
dotnet add package CQRSharp.Redis                 # Redis stores
dotnet add package CQRSharp.EntityFrameworkCore   # EF Core (relational) stores and unit of work
```

CQRSharp targets **net8.0, net9.0 and net10.0**. `CQRSharp.Abstractions`, the contracts package, also targets
`netstandard2.0`, so a contracts-only library can declare requests and notifications for any .NET consumer.

## Your first app

This is the program `dotnet new cqrsharp` scaffolds: it registers CQRSharp, sends one query and prints the result. It runs
on a .NET 8+ console project that references `CQRSharp` and `Microsoft.Extensions.Hosting`. The CQRSharp types need no
`using` directives thanks to the package's [global usings](#global-usings); only the host and DI usings are explicit.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddCqrsGenerated();              // the dispatcher, every discovered handler, validation and exception handling

using var host = builder.Build();
await host.StartAsync();

using (var scope = host.Services.CreateScope())   // the dispatcher is scoped: resolve it from a scope
{
    var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
    var greeting = await dispatcher.Send(new Greet("world"));
    Console.WriteLine(greeting);                  // Hello, world!
}

await host.StopAsync();

// The query and its handler. The source generator discovers and registers the handler.
public sealed class Greet(string name) : QueryBase<string>
{
    public string Name { get; } = name;
}

public sealed class GreetHandler : IQueryHandler<Greet, string>
{
    public Task<string> Handle(Greet query, CancellationToken cancellationToken)
        => Task.FromResult($"Hello, {query.Name}!");
}
```

To create the project from the template:

```bash
dotnet new install CQRSharp.Templates
dotnet new cqrsharp -n MyApp
```

The rest of this guide takes the pieces apart and adds queries, streams, notifications, behaviors and an outbox.

## Register CQRSharp

`AddCqrsGenerated` is written into your assembly by the source generator. It registers the dispatcher and every command,
query, stream and notification handler the generator discovered, in this assembly and in the CQRSharp assemblies it
references. There is no assembly scanning at runtime.

```csharp
// The dispatcher, the discovered handlers, and the validation and exception-handling behaviors.
services.AddCqrsGenerated();

// The same, configured through the fluent builder. Validation and exception handling stay on unless turned off.
services.AddCqrsGenerated(builder => builder
    .UseLogging()
    .ValidateOnStart());
```

[Configuration](configuration.md) covers both forms and every builder verb.

## Global usings

On a project with `ImplicitUsings` enabled (the default for .NET 8+ projects), the meta-package adds `CQRSharp` and
`CQRSharp.Pipelines` as global usings. The request base classes, handler interfaces, `CommandResult`, `INotification`,
`ICqrsDispatcher`, `AddCqrsGenerated`, the builder and its verbs are then in scope everywhere. `CQRSharp.Persistence`,
which only store and unit-of-work implementations need, is not a global using. The [namespace map](README.md#namespaces)
lists what lives where.

To keep implicit usings but drop CQRSharp's:

```xml
<PropertyGroup>
  <CQRSharpImplicitUsings>false</CQRSharpImplicitUsings>
</PropertyGroup>
```

The snippets in these docs assume the global usings are active.

## Your first command

A **command** expresses an intent to change state. Derive it from `CommandBase` and handle it with
`ICommandHandler<TCommand>`. A command's result is a [`CommandResult`](requests-and-handlers.md#commandresult): success,
or a failure with a kind (`NotFound`, `Conflict`, `Validation`, ...) and a message.

```csharp
public sealed class CreateUser : CommandBase
{
    public required string Name { get; init; }
}

public sealed class CreateUserHandler : ICommandHandler<CreateUser>
{
    public Task<CommandResult> Handle(CreateUser command, CancellationToken cancellationToken)
    {
        // ... persist the user ...
        return Task.FromResult(CommandResult.FromSuccess());
        // or: CommandResult.Conflict("Name already taken")
    }
}
```

Send it through `ICqrsDispatcher`. A handler reports a failure by returning it, so `Send` does not throw for one: await
the result and check `IsSuccess`.

```csharp
public sealed class UserService(ICqrsDispatcher cqrs)
{
    public async Task<string?> CreateAsync(string name, CancellationToken cancellationToken)
    {
        CommandResult result = await cqrs.Send(new CreateUser { Name = name }, cancellationToken);
        return result.IsSuccess ? null : result.ErrorMessage;   // result.ErrorKind says what kind of failure
    }
}
```

In an ASP.NET Core endpoint, add `CQRSharp.AspNetCore` and `using CQRSharp.AspNetCore;`, then
`return result.ToHttpResult();`: it picks the status code from the error kind and writes a ProblemDetails body
([ASP.NET Core](aspnetcore.md)).

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
    public Task<string> Handle(GetUserName query, CancellationToken cancellationToken)
        => Task.FromResult("Ada");
}

// string name = await cqrs.Send(new GetUserName { Id = id });
```

## Streaming requests

A **streaming request** yields a sequence asynchronously. Derive it from `StreamRequestBase<TItem>`, handle it with
`IStreamRequestHandler<TRequest, TItem>`, and dispatch it with `Stream`, not `Send`.

```csharp
public sealed class TailLog : StreamRequestBase<string>;

public sealed class TailLogHandler : IStreamRequestHandler<TailLog, string>
{
    public async IAsyncEnumerable<string> Handle(
        TailLog request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(100, cancellationToken);
            yield return $"line {i}";
        }
    }
}

// await foreach (var line in cqrs.Stream(new TailLog())) { ... }
```

`[EnumeratorCancellation]` is in `System.Runtime.CompilerServices`, which is not an implicit using.

> The C# compiler accepts a stream request in `Send(...)`, but the dispatcher throws for it at runtime. The **CQRA004**
> analyzer makes this a build error, and its code fix switches the call to `Stream(...)`.

## Notifications

A **notification** is a one-to-many event. Implement `INotification`, handle it with any number of
`INotificationHandler<TNotification>` classes, and publish it with `Publish`.

```csharp
public sealed record UserCreated(Guid Id) : INotification;

public sealed class SendWelcomeEmail : INotificationHandler<UserCreated>
{
    public Task Handle(UserCreated notification, CancellationToken cancellationToken)
        => Task.CompletedTask;   // ... send the email ...
}

// await cqrs.Publish(new UserCreated(id));
```

Handlers run one after another by default, in the publishing DI scope. [Notifications](notifications.md) covers the
other publish strategies and which handlers a notification reaches.

## Adding behaviors

Cross-cutting behaviors are configured through the builder. The order of the verbs does not matter: each behavior has a
fixed place in the pipeline.

```csharp
services.AddCqrsGenerated(builder => builder
    .UseLogging()
    .UseTimeout(o => o.Timeout = TimeSpan.FromSeconds(30))
    .UseResilience(o => o.MaxRetries = 3));
```

Validation and exception handling need no verb in this form. Some behaviors act only on requests that opt in: resilience
retries requests that implement `IRetryableRequest`, for example. [Pipeline behaviors](pipeline-behaviors.md) describes
each behavior and how to write your own.

## Adding an outbox

The outbox is **off** until you call `UseOutbox`, which turns it on and chooses its store in one step:

```csharp
services.AddCqrsGenerated(builder => builder
    .UseOutbox(o => o.UseInMemoryStore()));   // development and tests: not durable
```

For durable storage, use `o.UseRedis(...)` or `o.UseEntityFrameworkCore<MyDbContext>()` instead. Only notifications the
registered notification serializer can name go through the outbox; with the generated serializer that means types marked
`[NotificationName("...")]`. Everything else is still dispatched in process. [The outbox](outbox.md) has the details.

## Where to go next

- [Requests and handlers](requests-and-handlers.md): the request model, `CommandResult` and the request context.
- [Configuration](configuration.md): every builder verb and option.
- [Pipeline behaviors](pipeline-behaviors.md): the built-in behaviors and custom ones.
- [Diagnostics and validation](diagnostics.md): the analyzers and startup checks.
