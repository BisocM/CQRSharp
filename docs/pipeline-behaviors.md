# Pipeline behaviors

Every request flows through a **behavior pipeline** before reaching its handler — a chain of
middleware, each wrapping the next, with the handler at the center. CQRSharp ships a set of opt-in
built-in behaviors (logging, validation, exception handling, rate limiting, resilience, timeout, unit
of work, idempotency) and lets you write your own.

- [The pipeline model](#the-pipeline-model)
- [Behavior interfaces](#behavior-interfaces)
- [Execution order](#execution-order)
- [Built-in behaviors](#built-in-behaviors)
- [Validation](#validation)
- [Exception handling](#exception-handling)
- [Custom behaviors](#custom-behaviors)
- [Pipeline exemptions](#pipeline-exemptions)
- [Interceptors (pre/post-handler attributes)](#interceptors-prepost-handler-attributes)

## The pipeline model

A behavior receives the request and a `next` delegate. It does work before and/or after calling
`next(...)`, which runs the rest of the pipeline (the inner behaviors, then the handler). This is the
classic "Russian doll" model:

```
ExceptionHandling( RateLimiting( Logging( Validation( Resilience( Idempotency( UnitOfWork( Timeout(
    handler
)))))))) )
```

Each layer can short-circuit (return without calling `next`), transform the result, catch exceptions,
or retry.

## Behavior interfaces

There are two behavior contracts — one for `Send` (commands/queries), one for `Stream`:

```csharp
public interface IPipelineBehavior<TRequest, TResult>
{
    Task<TResult> Handle(TRequest request, Func<CancellationToken, Task<TResult>> next, CancellationToken ct);
}

public interface IStreamPipelineBehavior<TRequest, TItem>
{
    IAsyncEnumerable<TItem> Handle(TRequest request, Func<CancellationToken, IAsyncEnumerable<TItem>> next, CancellationToken ct);
}
```

Most built-ins ship both a request and a stream variant so the same cross-cutting concern applies to
streaming requests.

## Execution order

Order is **deterministic** and controlled by priority. A behavior may implement
`IPrioritizedPipelineBehavior`:

```csharp
public interface IPrioritizedPipelineBehavior
{
    const int DefaultPriority = int.MaxValue / 2;   // used by behaviors that don't implement this
    int PipelineExecutionPriority { get; }          // LOWER runs earlier (further from the handler)
}
```

The built-ins pin themselves to well-known priorities (`CqrsPipelinePriorities`), producing this order
from outermost to the handler:

| Order | Behavior | Priority | Why here |
| --- | --- | --- | --- |
| 1 (outermost) | Exception handling | `int.MinValue` | Wraps everything so it can observe any failure. |
| 2 | Rate limiting | `int.MinValue + 1` | Reject a throttled request before any other work. |
| 3 | Logging | `-100` | Measures the full pipeline, including retries. |
| 4 | Validation | `-50` | Reject invalid input before transactions or the handler. |
| 5 | Resilience (retries) | `50` | Wraps the unit of work, so each retry gets a fresh transaction. |
| 6 | Idempotency | `75` | Inside resilience (a released claim can retry), before the UoW (a duplicate short-circuits before a transaction opens). |
| 7 | Unit of work | `100` | Opens the transaction around the handler. |
| 8 (innermost) | Timeout | `300` | Bounds the handler itself, not the outer behaviors. |
| 9 | *your custom behaviors* | `int.MaxValue / 2` | Non-prioritized behaviors run just before the handler, ordered among themselves by type name. |

When two behaviors share a priority, ties break by full type name (stable, deterministic). To place a
custom behavior at a specific point, implement `IPrioritizedPipelineBehavior` and reference the
`CqrsPipelinePriorities` constants.

## Built-in behaviors

All built-ins are off by default and enabled through the [fluent builder](configuration.md):

| Builder verb | Behavior | Details |
| --- | --- | --- |
| `UseLogging()` | Logs request start, completion (with elapsed time), and failure. | — |
| `UseValidation()` | Runs every `IRequestValidator<TRequest>`; throws on failures. | [below](#validation) |
| `UseExceptionHandling()` | Request-level exception hooks that can observe or convert exceptions. | [below](#exception-handling) |
| `UseRateLimiting(o => …)` | Token-bucket throttling keyed on the request context. | [below](#rate-limiting) |
| `UseResilience(o => …)` | Automatic retries for `IRetryableRequest`. | [Idempotency & resilience](idempotency-and-resilience.md) |
| `UseTimeout(o => …)` | Per-request timeout that bounds the handler. | [Idempotency & resilience](idempotency-and-resilience.md#timeouts) |
| `UseUnitOfWork<TUoW>(…)` | Wraps transactional requests in a UoW transaction. | [Unit of work](unit-of-work.md) |
| `UseIdempotency(…)` | At-most-once processing for `IIdempotentRequest`. | [Idempotency & resilience](idempotency-and-resilience.md) |

### Rate limiting

`UseRateLimiting` enables a continuously-refilling token-bucket limiter. It throttles only requests
whose **context** implements `IRateLimitedContext` (the context supplies the user/request identifiers
the limiter keys on). A request whose context does not implement it passes through unthrottled — the
**CQRA007** analyzer warns when rate limiting is configured but a request's context doesn't opt in.

```csharp
.UseRateLimiting(o =>
{
    o.MaxTokens              = 100;     // bucket capacity
    o.ReplenishRatePerSecond = 10;      // tokens added per second
    o.MaxEntries             = 10_000;  // distinct keys tracked
})
```

Invalid options fail fast at host start via options validation (an `OptionsValidationException`).

## Validation

`UseValidation()` runs every registered validator for a request before the handler. Implement
`IRequestValidator<TRequest>`:

```csharp
public interface IRequestValidator<in TRequest> where TRequest : IRequest
{
    Task<ValidationFailure[]> ValidateAsync(TRequest request, CancellationToken ct);
}
```

```csharp
public sealed class CreateUserValidator : IRequestValidator<CreateUser>
{
    public Task<ValidationFailure[]> ValidateAsync(CreateUser r, CancellationToken ct)
        => Task.FromResult(string.IsNullOrWhiteSpace(r.Name)
            ? new[] { new ValidationFailure("NAME_REQUIRED", "Name is required.", nameof(r.Name)) }
            : Array.Empty<ValidationFailure>());
}
```

Register validators in DI (`services.AddTransient<IRequestValidator<CreateUser>, CreateUserValidator>()`).
A request may have several validators; their failures are **aggregated**. If any validator returns a
failure, the behavior throws `RequestValidationException` **before** the handler runs:

```csharp
public sealed class RequestValidationException : Exception
{
    public Type RequestType { get; }
    public IReadOnlyList<ValidationFailure> Failures { get; }
}
```

Catch it at your API boundary and translate `Failures` into a 400 response. A validator that returns
`Array.Empty<ValidationFailure>()` passes.

## Exception handling

`UseExceptionHandling()` enables request-level exception hooks: typed handlers that can **observe** an
exception (side effects) or **convert** it into a successful response. Two contracts:

```csharp
// Can convert the exception into a response.
public interface IRequestExceptionHandler<in TRequest, TResponse, in TException>
    where TRequest : IRequest<TResponse> where TException : Exception
{
    Task Handle(TRequest request, TException exception,
                RequestExceptionHandlerState<TResponse> state, CancellationToken ct);
}

// Side effects only — never suppresses the exception.
public interface IRequestExceptionAction<in TRequest, in TException>
    where TRequest : IRequest where TException : Exception
{
    Task Execute(TRequest request, TException exception, CancellationToken ct);
}
```

```csharp
public sealed class HandleDuplicate
    : IRequestExceptionHandler<CreateUser, CommandResult, DuplicateRequestException>
{
    public Task Handle(CreateUser r, DuplicateRequestException ex,
        RequestExceptionHandlerState<CommandResult> state, CancellationToken ct)
    {
        state.SetHandled(CommandResult.FromError("Already processed", 409));   // swallow + respond
        return Task.CompletedTask;
    }
}
```

Register the hooks in DI. If a handler calls `state.SetHandled(response)`, the exception is suppressed
and that response is returned to the caller; otherwise the exception propagates after all actions run.
Hook resolution is matched by request and exception type by the source generator — no reflection at
runtime.

## Custom behaviors

Write an open-generic behavior and register it through `builder.Services`. Implement
`IPrioritizedPipelineBehavior` to place it in the pipeline:

```csharp
public sealed class AuditBehavior<TRequest, TResult>
    : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior
{
    public int PipelineExecutionPriority => CqrsPipelinePriorities.Logging + 1;  // just inside logging

    public async Task<TResult> Handle(TRequest request,
        Func<CancellationToken, Task<TResult>> next, CancellationToken ct)
    {
        // ... before ...
        var result = await next(ct);
        // ... after ...
        return result;
    }
}

services.AddCqrsGenerated(b => b
    .UseLogging()
    .Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(AuditBehavior<,>)));
```

Behaviors are resolved fresh per dispatch and run on the dispatching scope. For a streaming concern,
implement `IStreamPipelineBehavior<TRequest, TItem>` and register it the same way.

## Pipeline exemptions

A request can opt **out** of a specific behavior with `[PipelineExemption]`. Use the **open-generic**
form — it exempts the behavior for every request it is closed over and is the idiomatic shorthand:

```csharp
[PipelineExemption(typeof(LoggingBehavior<,>))]   // this command skips logging
public sealed class Heartbeat : CommandBase;
```

- The **CQRA005** analyzer (error) flags an exemption whose target is **not** a pipeline behavior — it
  would silently have no effect.
- The **CQRA008** analyzer (info, with a code fix) suggests converting a verbose closed-generic
  exemption — `typeof(LoggingBehavior<Heartbeat, CommandResult>)` — to the open-generic form above.

Exemptions are surfaced on `RequestMetadata.PipelineExemptions` and in the
[diagnostics introspection API](diagnostics.md).

## Interceptors (pre/post-handler attributes)

Besides behaviors, you can attach **attribute-based interceptors** that run immediately around the
handler (inside the whole behavior pipeline). Implement `IPreHandlerAttribute` and/or
`IPostHandlerAttribute` on an attribute and apply it to a request:

```csharp
public interface IPreHandlerAttribute
{
    int PreHandlerExecutionPriority { get; }   // lower runs first
    Task OnBeforeHandle(IRequest request, IServiceProvider services, CancellationToken ct);
}
```

Pre-handlers run just before the handler (ordered by `PreHandlerExecutionPriority`); post-handlers run
just after. They receive the `IServiceProvider` so they can resolve dependencies. The generator records
them on `RequestMetadata.PreHandlers` / `PostHandlers`. Interceptors are best for small, declarative,
per-request concerns that read naturally as an attribute on the request type; reach for a **behavior**
when the concern is cross-cutting across many requests or needs to wrap (not just bracket) the handler.
