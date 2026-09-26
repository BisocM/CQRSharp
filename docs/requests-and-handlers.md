# Requests and handlers

This page covers the request kinds, the handler interfaces, the dispatch façade, `CommandResult`, and the request
context.

- [The request kinds](#the-request-kinds)
- [Base classes](#base-classes)
- [Handlers](#handlers)
- [The dispatcher](#the-dispatcher)
- [CommandResult](#commandresult)
- [Value-returning commands](#value-returning-commands)
- [Request context](#request-context)
- [Custom contexts](#custom-contexts)
- [Requests are single-use](#requests-are-single-use)

## The request kinds

Everything dispatchable implements `IRequest`. There are four kinds:

| Marker | Returns | Dispatched with | Purpose |
| --- | --- | --- | --- |
| `ICommand` | `CommandResult` | `Send` | Change state. |
| `ICommand<TResult>` | `CommandResult<TResult>` | `Send` | Change state **and** return a value no query can return. See [value-returning commands](#value-returning-commands). |
| `IQuery<TResult>` | `TResult` | `Send` | Read data; change nothing. |
| `IStreamRequest<TItem>` | `IAsyncEnumerable<TItem>` | `Stream` | Produce a sequence of items asynchronously. |

The response type is part of the type: `IRequest<out TResponse> : IRequest` carries it, and

```csharp
public interface ICommand : IRequest<CommandResult>, ICommandMarker;
public interface ICommand<TResult> : IRequest<CommandResult<TResult>>, ICommandMarker;
public interface IQuery<out TResult> : IRequest<TResult>;
public interface IStreamRequest<out TItem> : IStreamRequest, IRequest<IAsyncEnumerable<TItem>>;
```

That is why one `Send<TResponse>(IRequest<TResponse>)` method serves commands and queries: the compiler infers
`TResponse` as `CommandResult`, `CommandResult<TResult>` or the query's `TResult`. `ICommandMarker` is what both command
kinds share; the command lifecycle notifications build on it. A request counts as a command when it implements
`ICommandMarker` and is dispatched with a `CommandResult` or `CommandResult<TResult>`, which `ICommand` and
`ICommand<TResult>` guarantee.

`IRequest` has one member, the request's context, which the dispatcher sets (see [Request context](#request-context)):

```csharp
public interface IRequest
{
    IRequestContext? Context { get; set; }
}
```

You rarely implement these interfaces yourself; you derive from the base classes.

## Base classes

| Base class | Implements | Use for |
| --- | --- | --- |
| `CommandBase` | `ICommand` | A command with the default context. |
| `CommandBase<TContext>` | `ICommand` | A command with a custom context. |
| `ResultCommandBase<TResult>` | `ICommand<TResult>` | A value-returning command with the default context. |
| `ResultCommandBase<TResult, TContext>` | `ICommand<TResult>` | A value-returning command with a custom context. |
| `QueryBase<TResult>` | `IQuery<TResult>` | A query with the default context. |
| `QueryBase<TResult, TContext>` | `IQuery<TResult>` | A query with a custom context. |
| `StreamRequestBase<TItem>` | `IStreamRequest<TItem>` | A streaming request with the default context. |
| `StreamRequestBase<TItem, TContext>` | `IStreamRequest<TItem>` | A streaming request with a custom context. |

All of them derive from `RequestBase<TContext>`. The forms without a `TContext` fix it to `RequestContextBase`:

```csharp
public abstract class QueryBase<TResult> : QueryBase<TResult, RequestContextBase>;
```

```csharp
public sealed class CreateUser : CommandBase
{
    public required string Name { get; init; }
}

public sealed class GetUser : QueryBase<UserDto>
{
    public required Guid Id { get; init; }
}

public sealed class StreamUsers : StreamRequestBase<UserDto>;
```

## Handlers

Each request kind has one handler interface:

| Handler | Method | Returns |
| --- | --- | --- |
| `ICommandHandler<TCommand>` | `Handle(command, cancellationToken)` | `Task<CommandResult>` |
| `IResultCommandHandler<TCommand, TResult>` | `Handle(command, cancellationToken)` | `Task<CommandResult<TResult>>` |
| `IQueryHandler<TQuery, TResult>` | `Handle(query, cancellationToken)` | `Task<TResult>` |
| `IStreamRequestHandler<TRequest, TItem>` | `Handle(request, cancellationToken)` | `IAsyncEnumerable<TItem>` |

A handler does not name the context type: `request.Context` is already typed by the request's base class.

```csharp
public sealed class CreateUserHandler : ICommandHandler<CreateUser>
{
    public Task<CommandResult> Handle(CreateUser command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult.FromSuccess());
}

public sealed class GetUserHandler(IUserRepository users) : IQueryHandler<GetUser, UserDto>
{
    public Task<UserDto> Handle(GetUser query, CancellationToken cancellationToken)
        => users.LoadAsync(query.Id, cancellationToken);
}

public sealed class StreamUsersHandler(IUserRepository users) : IStreamRequestHandler<StreamUsers, UserDto>
{
    public IAsyncEnumerable<UserDto> Handle(StreamUsers request, CancellationToken cancellationToken)
        => users.StreamAllAsync(cancellationToken);
}
```

The source generator finds every closed, non-abstract handler class in the compilation and registers it, so there is no
registration call to write and no assembly scanning at runtime. Handlers are registered as transients by their concrete
type; to choose another lifetime, register the handler yourself (see
[Hosting and lifetimes](configuration.md#hosting-and-lifetimes)). What the generator cannot register it reports: a handler
less accessible than `internal` (**CQRGEN006**), an open-generic handler (**CQRGEN009**), and two handlers for one request
(**CQRGEN004**, an error). A request with no handler gets **CQRGEN003** where it is declared and **CQRA003** where it is
dispatched; dispatching it throws at runtime. Every code is listed in [Diagnostics](diagnostics.md).

A handler returns the result; the lifecycle notifications, the pipeline behaviors and the propagation of the result are
the framework's job.

## The dispatcher

`ICqrsDispatcher` is the one façade for dispatch:

```csharp
public interface ICqrsDispatcher
{
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
    Task<object?> Send(object request, CancellationToken cancellationToken = default);

    IAsyncEnumerable<TItem> Stream<TItem>(IStreamRequest<TItem> request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<object?> Stream(object request, CancellationToken cancellationToken = default);

    Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification;
}
```

- **`Send`** runs a command or query through its pipeline and returns the result. It throws
  `InvalidOperationException` for a streaming request (the **CQRA004** analyzer makes that a build error).
- **`Stream`** runs a streaming request. The stream runs as it is enumerated, on the flow that enumerates it.
- **`Publish`** delivers a notification to its handlers (see [Notifications](notifications.md)).
- The `object` overloads dispatch by the request's runtime type and return boxed results, for code that only has the
  request as `object` (a generic message endpoint). `Send(object)` throws `ArgumentException` for an object that is not
  an `IRequest`, and `Stream(object)` for one that is not an `IStreamRequest`. Prefer the typed overloads elsewhere.

`ICqrsDispatcher` is registered as a scoped service: resolve it from a DI scope, never from the root provider
([Hosting and lifetimes](configuration.md#hosting-and-lifetimes)).

```csharp
public sealed class Endpoints(ICqrsDispatcher cqrs)
{
    public Task<CommandResult> Create(CreateUser command) => cqrs.Send(command);
    public Task<UserDto> Get(GetUser query) => cqrs.Send(query);
    public IAsyncEnumerable<UserDto> List(StreamUsers request) => cqrs.Stream(request);
}
```

## CommandResult

A command's result is a `CommandResult`, an immutable record that describes the outcome, so behaviors and callers can
inspect it without a result type per command:

```csharp
public record CommandResult
{
    public bool IsSuccess { get; }
    public CommandErrorKind ErrorKind { get; }                          // None on success, never None on failure
    public string? ErrorMessage { get; }
    public int? ErrorCode { get; }                                      // application-defined; CQRSharp never interprets it
    public IReadOnlyList<ValidationFailure> ValidationFailures { get; } // the failures of a Validation result

    public static CommandResult FromSuccess();
    public static CommandResult FromError(string errorMessage, int? errorCode = null);   // kind Failure
    public static CommandResult FromError(CommandErrorKind errorKind, string errorMessage, int? errorCode = null);
    public static CommandResult NotFound(string errorMessage, int? errorCode = null);
    public static CommandResult Conflict(string errorMessage, int? errorCode = null);
    public static CommandResult Unauthorized(string errorMessage, int? errorCode = null);
    public static CommandResult Forbidden(string errorMessage, int? errorCode = null);
    public static CommandResult Unavailable(string errorMessage, int? errorCode = null);
    public static CommandResult Invalid(params ValidationFailure[] failures);
    public static CommandResult Invalid(IReadOnlyList<ValidationFailure> failures, string? errorMessage = null, int? errorCode = null);
}
```

```csharp
return CommandResult.FromSuccess();
return CommandResult.NotFound($"Order {command.OrderId} does not exist.");
return CommandResult.Conflict("Order already shipped.", errorCode: 1002);
return CommandResult.Invalid(new ValidationFailure("QUANTITY_RANGE", "Quantity must be positive.", nameof(command.Quantity)));
```

A failure has a **kind** (`Failure`, `Validation`, `NotFound`, `Conflict`, `Unauthorized`, `Forbidden` or
`Unavailable`), so a caller can react to the kind of failure without parsing the message or agreeing on error-code
conventions; `CQRSharp.AspNetCore` maps each kind to an HTTP status ([ASP.NET Core](aspnetcore.md#result-mapping)).
Report the kind through the factory, and keep `ErrorCode` for your own codes: a number passed as `ErrorCode` is never read
as an HTTP status. `FromError(CommandErrorKind.None, ...)` throws `ArgumentOutOfRangeException`, since `None` is not a
failure.

A `Validation` failure carries its `ValidationFailure`s as data: the same type the validation behavior throws in
`RequestValidationException`, so validation a handler performs itself reports like validation the pipeline performed.
`Invalid(...)` without a message uses `"Validation failed."`. Two results are equal when every part of the outcome is,
validation failures included.

The constructor is protected: create results through the factories. `CommandResult` describes an outcome, not a payload.
When a command's caller needs data, read it with a query; the one exception is covered next.

## Value-returning commands

Occasionally a command changes state **and** produces a value that **no query can return**: a secret created by the
operation and never stored in readable form (a one-time API key whose hash alone is stored, a token shown once). For that
case, derive the request from `ResultCommandBase<TResult>` (it implements `ICommand<TResult>`) and handle it with
`IResultCommandHandler<TCommand, TResult>`, which returns a `CommandResult<TResult>`: the outcome plus the value on
success.

```csharp
using System.Security.Cryptography;
using System.Text;

public sealed class MintApiKey : ResultCommandBase<string>
{
    public required Guid UserId { get; init; }
}

public sealed class MintApiKeyHandler(IApiKeyStore keys) : IResultCommandHandler<MintApiKey, string>
{
    public async Task<CommandResult<string>> Handle(MintApiKey command, CancellationToken cancellationToken)
    {
        var plaintext = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await keys.SaveHashAsync(command.UserId, SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)), cancellationToken);
        return CommandResult<string>.FromSuccess(plaintext);   // only the hash was stored
    }
}

// CommandResult<string> result = await cqrs.Send(new MintApiKey { UserId = id });
// if (result.IsSuccess) ShowOnce(result.Value!);
```

`CommandResult<TResult>` carries everything `CommandResult` does plus `Value` (set on success, `default` on failure), and
has the same factories (`FromSuccess(value)`, `FromError(...)`, `NotFound(...)`, `Invalid(...)`, ...). Its public
constructor takes every part of the outcome, so System.Text.Json, source generation included, can round-trip it (for
[idempotency replay](idempotency-and-resilience.md)). Its `ToString()` never prints `Value`, which may be a secret. For a
custom context, derive from `ResultCommandBase<TResult, TContext>`.

A value-returning command is a command: it publishes the command lifecycle notifications, whose `Command` property is
typed `ICommandMarker` and whose `Result` is the `CommandResult<TResult>` typed as `CommandResult`
([Notifications](notifications.md#lifecycle-notifications)).

> **Use this sparingly.** It relaxes the command/query split. For any value a query *can* return, return a plain
> `CommandResult` and read the value with an `IQuery<TResult>`.

## Request context

Every request carries an `IRequestContext`: data about who and when the request runs for, which travels with it through
the pipeline. The default context is `RequestContextBase`:

```csharp
public class RequestContextBase : IRequestContext
{
    public DateTime CreatedAt { get; }   // UTC, from the application's TimeProvider
}
```

The dispatcher creates the context from the context type's factory and sets `request.Context` before any behavior or the
handler runs. `CreatedAt` comes from the registered `TimeProvider`, so it is deterministic under a fake clock. Inside a
handler, read it from the request:

```csharp
public Task<CommandResult> Handle(CreateUser command, CancellationToken cancellationToken)
{
    var createdAt = command.Context!.CreatedAt;
    // ...
}
```

The dispatcher always replaces whatever context the request arrived with. `RequestBase<TContext>.Context` has no public
setter, so a model binder or a JSON deserializer cannot fill it: a client cannot supply a user or tenant through a request
body. Code that runs a handler without the dispatcher, such as a unit test, sets it through the interface:

```csharp
var command = new CreateOrder { Amount = 10m };
((IRequest)command).Context = new TenantContext { TenantId = "acme" };
```

That setter accepts `null` and throws `ArgumentException` for a context of another type.

## Custom contexts

To carry your own data (a tenant, the calling user, a correlation id), derive a context from `RequestContextBase` (or
implement `IRequestContext`), and derive the request from a base class that takes a `TContext`:

```csharp
public sealed class TenantContext : RequestContextBase
{
    public required string TenantId { get; init; }
}

public sealed class CreateOrder : CommandBase<TenantContext>
{
    public required decimal Amount { get; init; }
}

public sealed class CreateOrderHandler : ICommandHandler<CreateOrder>
{
    public Task<CommandResult> Handle(CreateOrder command, CancellationToken cancellationToken)
    {
        var tenant = command.Context!.TenantId;   // typed as TenantContext
        return Task.FromResult(CommandResult.FromSuccess());
    }
}
```

The context comes from an `IRequestContextFactory<TContext>`:

```csharp
public interface IRequestContextFactory<TContext> where TContext : IRequestContext
{
    ValueTask<TContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken);
}
```

```csharp
public sealed class TenantContextFactory(ICurrentTenant currentTenant) : IRequestContextFactory<TenantContext>
{
    public ValueTask<TenantContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken)
        => new(new TenantContext { TenantId = currentTenant.Id });
}
```

- **Every custom context needs a factory.** A request whose context type is not `RequestContextBase`
  (`CommandBase<TContext>`, `ResultCommandBase<TResult, TContext>`, `QueryBase<TResult, TContext>` or
  `StreamRequestBase<TItem, TContext>`) has no fallback: without a factory for its context type, dispatching it throws
  `InvalidOperationException` ("No IRequestContextFactory&lt;...&gt; is registered for context type ..."). The **CQRA011**
  analyzer warns about such a request at build time. The built-in factory serves only the default-context forms
  (`CommandBase`, `ResultCommandBase<TResult>`, `QueryBase<TResult>` and `StreamRequestBase<TItem>`).
- **Declaring a factory registers it.** The source generator registers every non-generic factory class it finds, as a
  transient. A context type has one factory: two in one assembly are an error (**CQRGEN018**); the composing assembly's
  factory replaces a referenced assembly's, and two in referenced assemblies with none in the composing one are reported
  as **CQRGEN019**. A factory you register yourself, before or after `AddCqrsGenerated`, replaces the discovered ones. An
  `IRequestContextFactory<RequestContextBase>` of your own replaces the built-in one.
- **Identity comes from the factory.** Read the user or the tenant from a trusted ambient source (the current HTTP user,
  a scoped service), never from the request. The sample's `CustomRequestContextFactory` reads a scoped `CurrentUser`.
- **The context is the caller's.** The factory is resolved from the scope of the dispatcher the request is sent through,
  and runs on the flow that sends it, before the request is queued (`RunMode.Queued`) or given a scope of its own
  (`ExecutionScopeMode.New`). Its scoped dependencies and the ambient state it reads (`IHttpContextAccessor`, any
  `AsyncLocal<T>`) are the caller's, wherever the handler then runs. A request a handler sends takes its context from that
  handler's scope. A request sent through a dispatcher whose scope has been disposed fails with `ObjectDisposedException`
  when its context type has a custom factory.
- **Load what the handlers need, once.** `CreateContextAsync` is awaited once, before the pipeline runs, so it can load
  request-scoped data asynchronously (a database, an HTTP API) and hand the handler a complete context. Load it in one
  batched call rather than making the context lazy-load. A factory that needs no I/O returns `new ValueTask<TContext>(context)`.
  A factory must not return `null`: the dispatch fails with `InvalidOperationException`.
- **Timestamps.** A context built with the parameterless `RequestContextBase()` constructor gets its `CreatedAt` from the
  application's `TimeProvider` when the factory hands it over; one built with `RequestContextBase(DateTime)` keeps the
  time it was given.

Some behaviors read the context: rate limiting, for example, limits only requests whose context implements
`IRateLimitedContext` ([Pipeline behaviors](pipeline-behaviors.md#rate-limiting)).

How a request is bound (its handler, context type, interceptors, exemptions and behaviors) is available at runtime from
`ICqrsDiagnostics.DescribeRequest` ([Diagnostics](diagnostics.md)).

## Requests are single-use

The dispatcher writes `Context` onto the request instance, so a **request object is single-use**. Create a new request
for each dispatch; do not dispatch one instance twice or share it across concurrent dispatches.

```csharp
await cqrs.Send(new CreateUser { Name = "Ada" });
await cqrs.Send(new CreateUser { Name = "Grace" });
```

Results (`CommandResult`, your query DTOs, streamed items) are yours to keep and pass around; only the request instance
is single-use.
