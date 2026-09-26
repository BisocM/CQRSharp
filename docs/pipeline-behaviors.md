# Pipeline behaviors

Every request passes through a **behavior pipeline** on its way to its handler: a chain of behaviors, each wrapping the
next, with the handler at the center. CQRSharp ships built-in behaviors (exception handling, rate limiting, logging,
validation, resilience, idempotency, unit of work, timeout) and runs your own.

- [The pipeline model](#the-pipeline-model)
- [Behavior interfaces](#behavior-interfaces)
- [Execution order](#execution-order)
- [Rate limiting](#rate-limiting)
- [Validation](#validation)
- [Exception handling](#exception-handling)
- [Custom behaviors](#custom-behaviors)
- [Pipeline exemptions](#pipeline-exemptions)
- [Interceptors (pre- and post-handler attributes)](#interceptors-pre--and-post-handler-attributes)

## The pipeline model

A behavior receives the request and a `next` delegate. It does its work before and after calling `next(...)`, which runs
the rest of the pipeline. With every built-in behavior enabled, a request runs through:

```
ExceptionHandling(
  RateLimiting(
    Logging(
      Validation(
        Resilience(
          Idempotency(
            UnitOfWork(
              Timeout(
                your behaviors(
                  lifecycle notifications, pre-handlers, handler, post-handlers
)))))))))
```

A behavior can short-circuit (return without calling `next`), transform the result, catch exceptions, or call `next`
again to retry. The [lifecycle notifications](notifications.md#lifecycle-notifications) and the
[interceptors](#interceptors-pre--and-post-handler-attributes) run at the center, around the handler.

## Behavior interfaces

There are two request behavior contracts, one for `Send` (commands and queries) and one for `Stream`, both in
`CQRSharp.Pipelines`:

```csharp
public delegate Task<TResult> RequestHandlerDelegate<TResult>(CancellationToken cancellationToken = default);

public interface IPipelineBehavior<in TRequest, TResult> where TRequest : IRequest
{
    Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken);
}

public delegate IAsyncEnumerable<TItem> StreamHandlerDelegate<TItem>(CancellationToken cancellationToken = default);

public interface IStreamPipelineBehavior<in TRequest, TItem> where TRequest : IRequest
{
    IAsyncEnumerable<TItem> Handle(TRequest request, StreamHandlerDelegate<TItem> next, CancellationToken cancellationToken);
}
```

`next`'s token is optional: `await next()` passes on the token the behavior received, and `await next(token)` passes
another one. Notifications have their own behavior contract,
[`INotificationPipelineBehavior<TNotification>`](notifications.md#notification-pipeline-behaviors).

Every built-in behavior has a request variant and a stream variant, so each concern applies to streaming requests too.

## Execution order

The order is fixed by priority. A behavior sets its own by implementing `IPrioritizedPipelineBehavior`:

```csharp
public interface IPrioritizedPipelineBehavior
{
    public const int DefaultPriority = int.MaxValue / 2;   // for behaviors that do not implement this interface
    int PipelineExecutionPriority { get; }                 // lower runs earlier, further from the handler
}
```

The built-ins use the constants in `CqrsPipelinePriorities`, which give this order, from the outermost behavior to the
handler, for requests and streams alike:

| Order | Behavior | Priority | Acts on | Why here |
| --- | --- | --- | --- | --- |
| 1 | Exception handling | `int.MinValue` | requests with [exception hooks](#exception-handling) | Wraps everything, so its hooks see every failure. |
| 2 | Rate limiting | `int.MinValue + 1` | requests whose context implements `IRateLimitedContext` | Rejects a throttled request before any other behavior does work. |
| 3 | Logging | `-100` | every request | Its elapsed time covers validation, retries, the unit of work and the handler. A throttled request is not logged by it. |
| 4 | Validation | `-50` | requests with [validators](#validation) | Rejects invalid input before any work. |
| 5 | Resilience | `50` | `IRetryableRequest` | Each retry starts after the failed attempt was rolled back and its idempotency claim released. |
| 6 | Idempotency | `75` | `IIdempotentRequest` | A duplicate is answered before a transaction opens. |
| 7 | Unit of work | `100` | `ITransactionalCommand`, `ITransactionalQuery` | Runs the timeout and the handler in a transaction. |
| 8 | Timeout | `300` | every request | The innermost built-in: bounds the handler and your default-priority behaviors, not the built-ins around it. |
| 9 | Your behaviors | `DefaultPriority` | what they choose | Behaviors without a priority run inside every built-in. |

Behaviors with the same priority are ordered by full type name. To place a custom behavior elsewhere, implement
`IPrioritizedPipelineBehavior` and use a `CqrsPipelinePriorities` constant (for example
`CqrsPipelinePriorities.Logging + 1`, just inside logging).

Each built-in is registered by its builder verb; the verbs, their signatures and their options are listed in
[Configuration](configuration.md#the-fluent-builder). Both forms of `AddCqrsGenerated`, the parameterless one included,
register exception handling and validation unless turned off with `UseExceptionHandling(false)` or
`UseValidation(false)`; every other behavior is registered only when its verb is called. Resilience, timeouts
and idempotency are described in [Idempotency and resilience](idempotency-and-resilience.md), the unit of work in
[Unit of work](unit-of-work.md), and the logging behavior's messages in [Observability](observability.md).

## Rate limiting

`UseRateLimiting(o => ...)` adds a token-bucket limiter. It limits only requests whose **context** implements
`IRateLimitedContext`; a request with any other context passes through untouched.

```csharp
public interface IRateLimitedContext : IRequestContext
{
    string UserId { get; }   // the key the request is limited under
}
```

The context factory must supply `UserId` from a trusted source: normally the authenticated user's id, and for an anonymous
caller another stable key such as the client address (a caller who can choose the key can sidestep the limit). A context
that implements the interface with an empty `UserId` makes the request fail with `InvalidOperationException`. See
[Custom contexts](requests-and-handlers.md#custom-contexts).

Each bucket holds up to `MaxTokens` tokens and refills continuously at `ReplenishRatePerSecond`; each request takes one. A
request that finds the bucket empty fails with `RateLimitExceededException`, whose `RetryAfter` is the time until the
bucket holds a token again, rounded up to whole milliseconds.

```csharp
.UseRateLimiting(o =>
{
    o.MaxTokens = 100;                          // burst size (default 3)
    o.ReplenishRatePerSecond = 10;              // refill rate (default 1)
    o.Scope = RateLimitScope.PerRequestType;    // one bucket per caller and request type (default: Global, one per caller)
    o.MaxEntries = 10_000;                      // buckets kept (default 10,000)
})
```

- `MaxEntries` is the number of buckets the limiter keeps (callers, or caller and request-type pairs). Nothing is dropped
  until that many exist. At the limit, buckets that have refilled completely are dropped first, which loses nothing; only
  when more than nine tenths are still in use are the least recently used ones dropped as well, down to nine tenths. A
  dropped caller starts again with a full bucket, so size `MaxEntries` comfortably above the callers active within one
  refill period (`MaxTokens / ReplenishRatePerSecond` seconds).
- `MaxIdleTime` (default 10 minutes; zero turns it off) is how long a bucket must go unused before the idle sweep may drop
  it; the sweep drops only buckets that have also refilled completely. `CleanupInterval` (default 5 minutes) is how often
  a timer runs the sweep; zero runs it from requests instead, at most every `MaxIdleTime / 2` and at least a second apart.
- Invalid options fail host start with `OptionsValidationException`.

## Validation

The validation behavior runs every validator registered for a request before the handler. Implement
`IRequestValidator<TRequest>`:

```csharp
public interface IRequestValidator<in TRequest> where TRequest : IRequest
{
    Task<ValidationFailure[]> ValidateAsync(TRequest request, CancellationToken cancellationToken);
}
```

```csharp
public sealed class CreateUserValidator : IRequestValidator<CreateUser>
{
    public Task<ValidationFailure[]> ValidateAsync(CreateUser request, CancellationToken cancellationToken)
        => Task.FromResult(string.IsNullOrWhiteSpace(request.Name)
            ? [new ValidationFailure("NAME_REQUIRED", "Name is required.", nameof(request.Name))]
            : Array.Empty<ValidationFailure>());
}
```

- **The generator registers validators.** Every public or internal, non-generic validator class it finds is registered,
  so there is nothing to call. Register one yourself only when the generator cannot see it (a private nested class, an
  open generic, or a class in an assembly the generator does not run in). A validator you register yourself that the
  generator also found runs once, as your registration.
- **Every validator runs**, one after another, and their failures are collected. When any failure is reported, the
  behavior throws `RequestValidationException` before the handler runs, carrying `RequestType` and every `Failures`
  entry. A validator that returns an empty array passes.
- **Validation is on by default**, in both forms of `AddCqrsGenerated`. It does nothing for a request without
  validators; turn it off with `UseValidation(false)`. The **CQRA012** analyzer warns about a validator in a project
  whose every registration turns validation off, and the startup validator reports **CQRDIAG005** for a request with
  validators (in any assembly) when the validation behavior is not registered.
- **FluentValidation validators** run inside the same behavior with `UseFluentValidation()` ([FluentValidation](fluentvalidation.md)).
- **Over HTTP**, `CQRSharp.AspNetCore` maps `RequestValidationException` to a 400 ProblemDetails response with the
  failures grouped by member ([ASP.NET Core](aspnetcore.md#exception-mapping)).

## Exception handling

The exception-handling behavior runs request-level exception hooks: typed classes that **observe** an exception (side
effects) or **handle** it by supplying the response. There are two contracts:

```csharp
// Side effects only; never suppresses the exception.
public interface IRequestExceptionAction<in TRequest, in TException>
    where TRequest : IRequest where TException : Exception
{
    Task Execute(TRequest request, TException exception, CancellationToken cancellationToken);
}

// Can handle the exception by supplying the response.
public interface IRequestExceptionHandler<in TRequest, TResponse, in TException>
    where TRequest : IRequest<TResponse> where TException : Exception
{
    Task Handle(TRequest request, TException exception, RequestExceptionHandlerState<TResponse> state,
        CancellationToken cancellationToken);
}
```

```csharp
public sealed class HandleDuplicate : IRequestExceptionHandler<CreateUser, CommandResult, DuplicateRequestException>
{
    public Task Handle(CreateUser request, DuplicateRequestException exception,
        RequestExceptionHandlerState<CommandResult> state, CancellationToken cancellationToken)
    {
        state.SetHandled(CommandResult.Conflict("Already processed."));   // the caller receives this result
        return Task.CompletedTask;
    }
}
```

- **The generator registers hooks**, like validators: every public or internal, non-generic hook class it finds. The
  behavior finds a request's hooks through the (request, exception type) pairs the generator recorded, so a hook class
  must be visible to it. A hook of your own registration of the same class replaces the discovered one.
- **`TResponse` must be the response the request is dispatched with**: `CommandResult` for a command,
  `CommandResult<TResult>` for a value-returning command, the query's result type, or `IAsyncEnumerable<TItem>` for a
  stream. A handler declared over another type never runs; the generator reports it as **CQRGEN017**.
- **Order.** When a request fails, every action whose exception type matches runs first (an action for `Exception` sees
  everything). Then the handlers run, from the most derived matching exception type up; the first to call
  `state.SetHandled(response)` ends it, the exception is suppressed, and that response is returned. Otherwise the
  exception propagates. Hooks for one request may be declared in several assemblies; each runs once.
- **The caller's cancellation is not handled:** an `OperationCanceledException` thrown once the caller's own token is
  cancelled passes through without running any hook. A cancellation the caller did not ask for (an `HttpClient`
  timeout, a handler's own linked token) is a failure like any other, and the hooks see it.
- Exception handling is on by default, in both forms of `AddCqrsGenerated`; turn it off with
  `UseExceptionHandling(false)`. The startup validator reports **CQRDIAG006** for a request with exception hooks when
  the exception-handling behavior is not registered.

## Custom behaviors

Write a behavior class and implement `IPrioritizedPipelineBehavior` if it needs a place other than the default:

```csharp
public sealed class AuditBehavior<TRequest, TResult>(ILogger<AuditBehavior<TRequest, TResult>> logger)
    : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    public int PipelineExecutionPriority => CqrsPipelinePriorities.Logging + 1;   // just inside logging

    public async Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken)
    {
        logger.LogInformation("Auditing {Request}", typeof(TRequest).Name);
        var result = await next();
        logger.LogInformation("Audited {Request}", typeof(TRequest).Name);
        return result;
    }
}

services.AddCqrsGenerated(b => b.Services
    .AddTransient(typeof(IPipelineBehavior<,>), typeof(AuditBehavior<,>)));
```

- **Open-generic behaviors are registered by you**, as above, through `builder.Services` or directly on the service
  collection, before or after `AddCqrsGenerated`. They are resolved for each dispatch from the dispatching scope, with the
  lifetime you registered.
- **Closed behaviors are registered by the generator.** A public or internal, non-generic class that implements
  `IPipelineBehavior<SomeRequest, SomeResult>` (or the stream contract) is registered like a validator, and runs for that
  request without a registration call. Closed notification behaviors are discovered the same way
  ([Notification pipeline behaviors](notifications.md#notification-pipeline-behaviors)).
- For a streaming concern, implement `IStreamPipelineBehavior<TRequest, TItem>` and register it the same way.

> **Native AOT and value-type results.** Without dynamic code, the container cannot close an open-generic behavior over a
> request whose result or streamed item is a value type (`IQuery<int>`, `IStreamRequest<Guid>`). The generator emits
> closed factories for the behaviors it can name, and a behavior it cannot close fails the dispatch rather than being
> skipped. [Native AOT](native-aot.md#value-type-results-and-notifications) has the rules.

## Pipeline exemptions

A request can opt **out** of a behavior with `[PipelineExemption]`. Name the behavior's open generic form to exempt it
whatever it is closed over:

```csharp
[PipelineExemption(typeof(LoggingBehavior<,>))]   // this command is not logged
public sealed class Heartbeat : CommandBase;
```

- An exemption applies to the request it is declared on, and to requests that derive from it (the attribute is
  inherited). The attribute can be repeated to exempt several behaviors. The closed form,
  `typeof(LoggingBehavior<Heartbeat, CommandResult>)`, works too.
- The **CQRA005** analyzer (error) reports an exemption that has no effect: the type is not a pipeline behavior, is
  abstract or an interface, never runs for the request (closed over another request or result, or a stream behavior on a
  command), or the attribute is on something that is not a request.
- The **CQRA008** analyzer (info, with a code fix) suggests the open-generic form for a closed exemption.
- `ICqrsDiagnostics.DescribeRequest` lists a request's exemptions and the behaviors they removed
  ([Diagnostics](diagnostics.md)).

## Interceptors (pre- and post-handler attributes)

An interceptor is an attribute on a request type that runs code just before or just after the handler, inside the whole
behavior pipeline. It implements `IPreHandlerAttribute`, `IPostHandlerAttribute`, or both:

```csharp
public interface IPreHandlerAttribute
{
    int PreHandlerExecutionPriority { get; }   // lower runs first
    Task OnBeforeHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

public interface IPostHandlerAttribute
{
    int PostHandlerExecutionPriority { get; }  // lower runs first
    Task OnAfterHandle(IRequest request, RequestOutcome outcome, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}
```

```csharp
[AttributeUsage(AttributeTargets.Class)]
public sealed class AuditAttribute(int priority) : Attribute, IPreHandlerAttribute, IPostHandlerAttribute
{
    public int PreHandlerExecutionPriority => priority;
    public int PostHandlerExecutionPriority => priority;

    public Task OnBeforeHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        => Task.CompletedTask;   // runs before the handler

    public Task OnAfterHandle(IRequest request, RequestOutcome outcome, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        => Task.CompletedTask;   // runs after the handler, with the value it returned or the exception the request failed with
}

[Audit(priority: 0)]
public sealed class CreateUser : CommandBase
{
    public required string Name { get; init; }
}
```

- **Order.** Pre-handlers run in `PreHandlerExecutionPriority` order and post-handlers in `PostHandlerExecutionPriority`
  order, lower first; equal priorities are ordered by full type name. Both receive the dispatching scope's
  `IServiceProvider` to resolve what they need. The sample's `CustomInterceptorAttribute` implements both interfaces.
- **Inheritance.** An interceptor on a base request class applies to derived requests, following the attribute's
  `AttributeUsage.Inherited` (true by default); with `AllowMultiple = false` the most derived one wins. Attributes on
  interfaces never apply.
- **Visibility.** Generated code rebuilds the attributes, so their types, and any type or enum their arguments name, must
  be public or internal to an assembly generated code can reach. The generator reports **CQRGEN016** (an error) for one it
  cannot rebuild, instead of dispatching the request without it.
- **A throwing pre-handler fails the request.** It gets the same failure path as a throwing handler: the post-handlers
  run with the exception, and *Failed* is published.

### Auditing on the outcome

`OnAfterHandle` runs on the success and the failure paths and receives the request's `RequestOutcome`: the value the
handler returned (`Result`, boxed; cast it to the request's result type) or the exception the request failed with
(`Exception`). `Threw` reports whether the dispatch failed, which is different from a business verdict inside a returned
value. A login command that returns a `CommandResult<LoginResult>` with a "bad credentials" verdict is a successful
dispatch that must still be audited as a failed login:

```csharp
public sealed class AuditLoginAttribute : Attribute, IPostHandlerAttribute
{
    public int PostHandlerExecutionPriority => 0;

    public Task OnAfterHandle(IRequest request, RequestOutcome outcome, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var audit = serviceProvider.GetRequiredService<IAuditSink>();

        if (outcome.Threw)
        {
            audit.Record("user.login.failed", success: false, error: outcome.Exception!.Message);
        }
        else if (outcome.Result is CommandResult<LoginResult> result)
        {
            // Null-safe: a failed CommandResult has no Value, and is recorded as a failed login.
            var ok = result is { Value.Verdict: LoginVerdict.Ok };
            audit.Record(ok ? "user.login" : "user.login.failed", success: ok);
        }

        return Task.CompletedTask;
    }
}
```

How post-handlers behave:

- **Every post-handler runs**, like nested `finally` blocks: one that throws never stops the others.
- **On a request that succeeded, the first post-handler that throws fails it.** The caller receives that exception,
  *Failed* is published with it, and the post-handlers after it see it as the outcome's `Exception`.
- **On a request that already failed**, a post-handler that throws is logged (event 1001, Warning) and never replaces
  the request's own exception.
- **Tokens.** On the success path `OnAfterHandle` receives the request's token; on the failure path it receives
  `CancellationToken.None`, because the request's token may be the one that was cancelled.
- **The failure path covers every stage after the request started.** A pre-handler that rejects the request (an
  authorization interceptor, say) reaches the post-handlers with `Threw` set, so a post-handler can run for a request
  whose own lower-priority pre-handler never ran. Treat `OnAfterHandle` like a `finally`.
- **Streams** follow the same contract: a stream that fails while it is enumerated reaches `OnAfterHandle` with the
  exception, and one enumerated to its end reaches it with a `null` `Result`. A consumer that stops enumerating early,
  without an error, runs no post-handler.

Interceptors suit small, declarative concerns that read naturally as an attribute on the request type. Use a behavior
when the concern applies across many requests, or has to wrap the rest of the pipeline rather than bracket the handler.
