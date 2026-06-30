# Requests and handlers

This page covers the heart of CQRSharp: the request taxonomy, the handler interfaces, the dispatch
façade, the request context, and `CommandResult`.

- [The request taxonomy](#the-request-taxonomy)
- [Base classes](#base-classes)
- [Handlers](#handlers)
- [The dispatcher](#the-dispatcher)
- [CommandResult](#commandresult)
- [Value-returning commands](#value-returning-commands)
- [Request context](#request-context)
- [Custom contexts](#custom-contexts)
- [RequestMetadata](#requestmetadata)
- [Requests are single-use](#requests-are-single-use)

## The request taxonomy

Everything dispatchable derives from `IRequest`. The framework distinguishes these kinds:

| Marker | Returns | Dispatched with | Purpose |
| --- | --- | --- | --- |
| `ICommand` | `CommandResult` | `Send` | Intent to change state. |
| `IQuery<TResult>` | `TResult` | `Send` | Read data; change nothing. |
| `IStreamRequest<TItem>` | `IAsyncEnumerable<TItem>` | `Stream` | Asynchronous sequence of items. |
| `ICommand<TResult>` | `CommandResult<TResult>` | `Send` | Change state **and** return a value no query can reproduce. See [value-returning commands](#value-returning-commands). |

The response type is encoded in the type system. `IRequest<out TResponse> : IRequest` carries the
response type, and:

```csharp
public interface ICommand : IRequest<CommandResult>;
public interface IQuery<out TResult> : IRequest<TResult>;
```

This is why a single `Send<TResponse>(IRequest<TResponse>)` method serves both commands and queries —
the compiler infers `TResponse` as `CommandResult` for a command and as `TResult` for a query.

`IRequest` itself exposes two properties the dispatcher fills in during processing:

```csharp
public interface IRequest
{
    IRequestContext? Context { get; set; }   // the per-request context (see below)
    RequestMetadata? Metadata { get; set; }  // compile-time structural metadata
}
```

You rarely implement these markers directly — you derive from the base classes.

## Base classes

| Base class | Implements | Use when |
| --- | --- | --- |
| `CommandBase` | `ICommand` | A command using the default context. |
| `CommandBase<TContext>` | `ICommand` | A command needing a custom context. |
| `ResultCommandBase<TResult>` | `ICommand<TResult>` | A value-returning command using the default context. |
| `ResultCommandBase<TResult, TContext>` | `ICommand<TResult>` | A value-returning command needing a custom context. |
| `QueryBase<TResult>` | `IQuery<TResult>` | A query using the default context. |
| `QueryBase<TResult, TContext>` | `IQuery<TResult>` | A query needing a custom context. |
| `StreamRequestBase<TItem>` | `IStreamRequest<TItem>` | A streaming request using the default context. |
| `StreamRequestBase<TItem, TContext>` | `IStreamRequest<TItem>` | A streaming request needing a custom context. |
| `RequestBase<TContext>` | `IRequest` | The shared base; you usually pick one of the above instead. |

The non-generic forms simply pin `TContext` to `RequestContextBase`:

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

Each request kind has a matching handler interface, with a three-type-parameter form that pins the
context type and a convenience form that defaults it to `RequestContextBase`:

| Handler | Method | Returns |
| --- | --- | --- |
| `ICommandHandler<TCommand>` / `ICommandHandler<TCommand, TContext>` | `Handle(command, ct)` | `Task<CommandResult>` |
| `IQueryHandler<TQuery, TResult>` / `IQueryHandler<TQuery, TResult, TContext>` | `Handle(query, ct)` | `Task<TResult>` |
| `IResultCommandHandler<TCommand, TResult>` / `IResultCommandHandler<TCommand, TResult, TContext>` | `Handle(command, ct)` | `Task<CommandResult<TResult>>` |
| `IStreamRequestHandler<TRequest, TItem>` / `IStreamRequestHandler<TRequest, TItem, TContext>` | `Handle(request, ct)` | `IAsyncEnumerable<TItem>` |

```csharp
public sealed class CreateUserHandler : ICommandHandler<CreateUser>
{
    public Task<CommandResult> Handle(CreateUser command, CancellationToken ct)
        => Task.FromResult(CommandResult.FromSuccess());
}

public sealed class GetUserHandler : IQueryHandler<GetUser, UserDto>
{
    public Task<UserDto> Handle(GetUser query, CancellationToken ct) => /* ... */;
}

public sealed class StreamUsersHandler : IStreamRequestHandler<StreamUsers, UserDto>
{
    public async IAsyncEnumerable<UserDto> Handle(StreamUsers request,
        [EnumeratorCancellation] CancellationToken ct) { /* yield ... */ }
}
```

A handler is registered with the container automatically — the source generator discovers every
`I*Handler<...>` implementation in the compilation and wires it into the dispatch tables. There is no
assembly scanning at runtime. If you dispatch a request that has **no** discoverable handler, the
**CQRA003** analyzer warns you at compile time, and dispatch throws at runtime.

> **Notice — `Handle` returns the *result* type.** The handler's job is to produce the result; the
> lifecycle notifications, pipeline behaviors, and result propagation are the framework's job.

## The dispatcher

`ICqrsDispatcher` is the single injected façade for all dispatch:

```csharp
public interface ICqrsDispatcher
{
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default);
    Task<object?>   Send(object request, CancellationToken ct = default);                 // untyped

    IAsyncEnumerable<TItem>    Stream<TItem>(IStreamRequest<TItem> request, CancellationToken ct = default);
    IAsyncEnumerable<object?>  Stream(object request, CancellationToken ct = default);     // untyped

    Task Publish<TNotification>(TNotification notification, CancellationToken ct = default)
        where TNotification : INotification;
}
```

- **`Send`** runs a command or query through the full behavior pipeline and returns its result.
- **`Stream`** runs a streaming request and returns the async sequence.
- **`Publish`** fans a notification out to all its handlers (see [Notifications](notifications.md)).
- The **untyped** `Send(object)` / `Stream(object)` overloads dispatch by runtime type and return a
  boxed result. Use them when you only have the request as `object` (e.g. a generic message endpoint);
  prefer the typed overloads everywhere else for compile-time safety.

```csharp
public sealed class Endpoints(ICqrsDispatcher cqrs)
{
    public Task<CommandResult> Create(CreateUser cmd)  => cqrs.Send(cmd);
    public Task<UserDto>       Get(GetUser q)          => cqrs.Send(q);
    public IAsyncEnumerable<UserDto> List(StreamUsers s) => cqrs.Stream(s);
}
```

## CommandResult

A command's result is always a `CommandResult` — a small immutable record that unifies success/failure
so behaviors and callers can inspect the outcome without a bespoke type per command:

```csharp
public record CommandResult
{
    public bool    IsSuccess    { get; }
    public string? ErrorMessage { get; }
    public int?    ErrorCode    { get; }

    public static CommandResult FromSuccess();
    public static CommandResult FromError(string errorMessage, int? errorCode = null);
}
```

```csharp
return CommandResult.FromSuccess();
return CommandResult.FromError("Name already taken", errorCode: 409);
```

The constructor is private — always use the factory methods. `CommandResult` is intentionally about
outcome, not payload — if a command needs to return *data*, model the read as a query. The one exception is
a value no query could ever reproduce; see [value-returning commands](#value-returning-commands).

## Value-returning commands

Occasionally a command both mutates state **and** produces a value that **no query can return** — a secret
minted at the instant of the operation and never persisted in readable form (a one-time API key whose hash
alone is stored, a generated token shown once). For that narrow case, derive the request from
`ResultCommandBase<TResult>` (it implements `ICommand<TResult>`) and handle it with
`IResultCommandHandler<TCommand, TResult>`, which returns a `CommandResult<TResult>` — the outcome plus the
value on success:

```csharp
public sealed class MintApiKey : ResultCommandBase<ApiKeySecret>
{
    public required Guid UserId { get; init; }
}

public sealed class MintApiKeyHandler : IResultCommandHandler<MintApiKey, ApiKeySecret>
{
    public Task<CommandResult<ApiKeySecret>> Handle(MintApiKey c, CancellationToken ct)
    {
        var (plaintext, hash) = ApiKey.Generate();
        _store.Save(c.UserId, hash);          // only the hash is persisted
        return Task.FromResult(CommandResult<ApiKeySecret>.FromSuccess(plaintext));
    }
}

// var r = await cqrs.Send(new MintApiKey { UserId = id });
// if (r.IsSuccess) ShowOnce(r.Value);        // the secret, the one time it ever exists
```

`CommandResult<TResult>` carries `IsSuccess`, `ErrorMessage`, `ErrorCode`, and `Value` (set on success); use
`FromSuccess(value)` / `FromError(message, code)`. Its `ToString()` deliberately never prints `Value` — it
may be a secret. For a custom context, derive from `ResultCommandBase<TResult, TContext>`.

> **Use this sparingly.** It deliberately relaxes the command/query split. For any value a query *can*
> return, return a plain `CommandResult` and read the value with an `IQuery<TResult>`. The analyzer
> **CQRA009** raises an informational reminder on every `ICommand<TResult>` so the choice stays deliberate.
>
> A value-returning command dispatches through the same path as a query, so it publishes the **query**
> lifecycle notifications (`QueryInitiated`/`Completed`/`Failed`), not the command ones.

## Request context

Every request carries an `IRequestContext` — a per-dispatch bag of ambient data that travels with the
request through the pipeline. The default implementation is `RequestContextBase`:

```csharp
public class RequestContextBase : IRequestContext
{
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
}
```

The dispatcher creates the context (via the source-generated context factory) and assigns it to
`request.Context` before the handler runs. Inside a handler you reach it through the request:

```csharp
public Task<CommandResult> Handle(CreateUser command, CancellationToken ct)
{
    var createdAt = command.Context!.CreatedAt;
    // ...
}
```

## Custom contexts

To carry your own ambient data (a tenant id, a correlation id, the calling user), implement
`IRequestContext` (or extend `RequestContextBase`) and **thread the context type in two places**: the
request base class and the handler interface.

```csharp
public sealed class TenantContext : RequestContextBase
{
    public required string TenantId { get; init; }
}

// 1) the request declares its context type
public sealed class CreateOrder : CommandBase<TenantContext>
{
    public required decimal Amount { get; init; }
}

// 2) the handler declares the same context type
public sealed class CreateOrderHandler : ICommandHandler<CreateOrder, TenantContext>
{
    public Task<CommandResult> Handle(CreateOrder command, CancellationToken ct)
    {
        var tenant = command.Context!.TenantId;   // strongly typed
        return Task.FromResult(CommandResult.FromSuccess());
    }
}
```

You supply the context instance through a context factory (`IRequestContextFactory<TContext>`). A
request that uses a custom context — i.e. derives from `CommandBase<TContext>` / `QueryBase<TContext>`
(or their result-command / stream / `ResultCommandBase<…, TContext>` siblings) with a `TContext` other
than `RequestContextBase` — **requires** a factory for that context type; there is **no** fallback for
custom contexts. The default `DefaultRequestContextFactory` only services the **non-generic**
`CommandBase` / `QueryBase` (which pin `TContext` to `RequestContextBase`). If a custom-context request
has no discoverable `IRequestContextFactory<TContext>`, the **first dispatch throws**
`InvalidOperationException` — *"No IRequestContextFactory&lt;…&gt; is registered for context type …"*.

The source generator auto-registers any factory it can see, so a discoverable factory type is enough —
no manual `services.Add…` call is required. The Sample's `CustomRequestContextFactory` is one such
factory (an `IRequestContextFactory<SampleRequestContext>`). To catch a missing factory at build time
rather than at runtime, the **CQRA011** analyzer (warning) flags any custom-context request that has no
discoverable factory.

> **The two type parameters must match.** If the request's `TContext` and the handler's `TContext`
> disagree, the **CQRA001** analyzer flags the mismatch at compile time — you don't find out at
> runtime.

Some behaviors key off the context type. For example, rate limiting only throttles a request whose
context implements `IRateLimitedContext`; if you configure rate limiting but a request's context does
not implement it, the **CQRA007** analyzer warns that the request passes through unthrottled.

### Async context hydration

When the context must be populated from an async source — a database, an HTTP API — derive from
`AsyncRequestContextFactory<TContext>` and override `CreateContextAsync`. The dispatcher awaits it once,
before the pipeline runs, so the handler receives a fully-populated context instead of blocking in the
factory or scattering lazy loads through the handler. Load the request-scoped data you need as a single
batched call:

```csharp
public sealed class UserContextFactory(IUserRepository users) : AsyncRequestContextFactory<UserContext>
{
    public override async ValueTask<UserContext> CreateContextAsync(IRequest request, CancellationToken ct)
        => new() { User = await users.LoadAggregateAsync(/* id from request */, ct) }; // one query: user + roles + …
}
```

Register it exactly like a synchronous factory (as `IRequestContextFactory<UserContext>`), or let the
generator discover it. Synchronous factories are unchanged — the contract's `CreateContextAsync` defaults to
wrapping `CreateContext`, so you only override it when creation needs I/O.

> Keep the context to request-scoped *data the handlers need*, loaded here once — not a general-purpose
> lazy-loading object graph. Hydrating the aggregate you need at this single awaited point is the goal;
> making the context itself lazy-load arbitrary navigations is what you're avoiding.

## RequestMetadata

`RequestMetadata` is the compile-time structural description of a request, produced by the source
generator and assigned to `request.Metadata` during dispatch:

```csharp
public sealed record RequestMetadata(
    Type   RequestType,
    Type?  HandlerType,
    IPreHandlerAttribute[]       PreHandlers,        // interceptors that run before the handler
    IPostHandlerAttribute[]      PostHandlers,       // interceptors that run after the handler
    PipelineExemptionAttribute[] PipelineExemptions, // behaviors this request opts out of
    Type?  ResultType,                               // null for commands
    Type?  ContextType);
```

You don't construct it — the framework does. It's surfaced so behaviors and the diagnostics API can
introspect how a request is wired (which interceptors apply, which behaviors it's exempt from). See
[Pipeline behaviors](pipeline-behaviors.md) for interceptors and exemptions, and
[Diagnostics](diagnostics.md) for the introspection API.

## Requests are single-use

Because the dispatcher writes `Context` and `Metadata` onto the request instance while processing it, a
**request object is single-use**. Construct a new request per dispatch; do not reuse one instance
across calls or share it across concurrent dispatches.

```csharp
// Good — a fresh instance per dispatch.
await cqrs.Send(new CreateUser { Name = "Ada" });
await cqrs.Send(new CreateUser { Name = "Grace" });
```

The result types (`CommandResult`, your query DTOs, streamed items) are yours to keep and pass around
freely; only the *request* instance is single-use.
