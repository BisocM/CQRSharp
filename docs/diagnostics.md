# Diagnostics &amp; validation

CQRSharp catches mistakes at three moments: **as you type** (Roslyn analyzers), **at build** (generator
diagnostics), and **at host start** (the startup validator). It also exposes a runtime **introspection
API** and a **health check** so you can see exactly how every request is wired.

- [Compile-time analyzers (CQRA)](#compile-time-analyzers-cqra)
- [Generator diagnostics (CQRGEN)](#generator-diagnostics-cqrgen)
- [Startup validation (CQRCONF)](#startup-validation-cqrconf)
- [The introspection API](#the-introspection-api)
- [Binding issues (CQRDIAG)](#binding-issues-cqrdiag)
- [Health checks](#health-checks)

## Compile-time analyzers (CQRA)

These run in the editor and on build, in the `CQRSharp.Usage` category. Several ship code fixes.

| ID | Severity | What it catches |
| --- | --- | --- |
| `CQRA001` | Warning | A handler's context type doesn't match the context its request declares (`RequestBase<TContext>` vs the handler's `TContext`). |
| `CQRA003` | Warning | A dispatched request has no discoverable handler in this compilation or referenced assemblies. |
| `CQRA004` | Error | A stream request is dispatched with `Send(...)` instead of `Stream(...)`. **Code fix:** switch to `Stream`. |
| `CQRA005` | Error | A `[PipelineExemption(typeof(T))]` target `T` is not a pipeline behavior, so the exemption has no effect. |
| `CQRA006` | Info | A published notification has no discoverable subscriber (legal, but often unintended). |
| `CQRA007` | Warning | Rate limiting is configured, but a request's context doesn't implement `IRateLimitedContext`, so it's never throttled. |
| `CQRA008` | Info | A `[PipelineExemption]` names a closed generic; the open-generic `typeof(Behavior<,>)` form is simpler. **Code fix:** convert it. |
| `CQRA009` | Info | A value-returning command (`ICommand<TResult>`) — a reminder to use it only for a value no query can reproduce; model a queryable read as `IQuery<TResult>` instead. |
| `CQRA010` | Warning | A project declares CQRSharp handlers but the source generator isn't running in it (no module is emitted), so its handlers won't be registered. Add the CQRSharp package to this project. |
| `CQRA011` | Warning | A request uses a custom context (`CommandBase<TContext>` / `QueryBase<TContext>` with a non-`RequestContextBase` `TContext`) but no `IRequestContextFactory<TContext>` is discoverable, so the first dispatch throws. Implement and register a factory (the generator auto-registers any it can see). |
| `CQRA012` | Warning | An `IRequestValidator<TRequest>` is declared but the validation behavior isn't enabled (no pipeline-pack verb where CQRSharp is registered), so the validator never runs and input reaches the handler **unvalidated**. Call `UseValidation()` (or any pack verb). |
| `CQRA013` | Info | A resilience/idempotency behavior is configured, but a request doesn't implement the opt-in marker (`IRetryableRequest` / `IIdempotentRequest`), so the behavior never applies to it. Ignore if opting out is intentional. |
| `CQRA014` | Error | `AddCqrs(...)` is called directly — it registers the dispatcher but not the generated handler routing, so the first `Send`/`Stream`/`Publish` throws. **Code fix:** call `AddCqrsGenerated(...)` instead. |
| `CQRA017` | Info | A type implements both `IPreHandlerAttribute` and `IPostHandlerAttribute`; it can be a single `ICommandInterceptor` (one combined pre+post interceptor). |

The handler/subscriber/context checks (`CQRA003`, `CQRA006`, `CQRA011`, and the validator check
`CQRA012`) work across assemblies because the generator emits assembly-level marker attributes for every
handled request, notification, and registered context factory.

## Generator diagnostics (CQRGEN)

These are reported by the source generator during compilation, in the `CQRSharp.Generators` category.

| ID | Severity | Meaning |
| --- | --- | --- |
| `CQRGEN002` | Error | Duplicate `[NotificationName]` on two notification types — stable names must be unique for outbox serialization. |
| `CQRGEN003` | Warning | No handler found for a request (add exactly one, or suppress if it's in another assembly). |
| `CQRGEN004` | Error | Multiple handlers found for one request — CQRSharp requires exactly one. |
| `CQRGEN005` | Error | A `[NotificationName]` notification can't be source-generated for AOT-safe outbox serialization (its shape is unsupported, or register a custom `INotificationSerializer`). |
| `CQRGEN006` | Warning | A handler implements a CQRSharp handler interface but is less accessible than `internal`, so generated registration skips it. Make it `public`/`internal`. |
| `CQRGEN007` | Error | A CQRSharp framework "well-known type" couldn't be resolved — usually mismatched package versions. |
| `CQRGEN008` | Info | `CQRSharp.Abstractions` is referenced but `CQRSharp.Core` isn't, so generation is skipped. Reference `CQRSharp.Core` (or the meta-package). |
| `CQRGEN009` | Warning | An open-generic handler (`Handler<T>`) is never registered — CQRSharp wires only closed, non-generic handlers, so it would otherwise fail with "no handler" at dispatch. Declare a concrete handler, or register it manually. |
| `CQRGEN010` | Warning | A handler is registered, but its binding is **skipped** because a bound request / result / context / notification type argument is less accessible than `internal` (the handler itself is accessible, but the type arg is `private`/file-scoped), so dispatching that request fails with "no handler" at runtime. Make the type `public` or `internal`. |
| `CQRGEN999` | Error | An unhandled exception in the generator (please report it). |

## Startup validation (CQRCONF)

The hosted **`CqrsStartupValidator`** inspects the wired-up configuration and every per-request binding
**once at host start**, turning silent fallbacks into loud, early failures. Enable it with
`ValidateOnStart()` (see [Configuration](configuration.md#startup-validation)).

| ID | Severity | Condition |
| --- | --- | --- |
| `CQRCONF001` | Error | An outbox mode is enabled, but a required outbox service (store / serializer / dispatcher) is not registered — the processor would sit idle. |
| `CQRCONF002` | Warning | Outbox mode is `Transactional`, but the registered `IUnitOfWork` is not an `IExplicitUnitOfWork`, so no active transaction can be detected and every publish degrades to in-process. |
| `CQRCONF003` | Warning | A handled notification has no `[NotificationName]` while an outbox mode is enabled, so it can't be persisted and bypasses the outbox. |
| `CQRCONF004` | Error | No generated registry is present although request bindings exist — `AddCqrsGenerated(...)` was not applied. |
| `CQRCONF005` | Warning | A request implements `IIdempotentRequest`, but no idempotency behavior is wired — the marker has no effect. Call `UseIdempotency(...)`. |
| `CQRCONF006` | Warning | A request implements `IRetryableRequest`, but no resilience behavior is wired — the marker has no effect. Call `UseResilience(...)`. |

The validator logs every issue with its code, then — per the `CqrsValidationPolicy` — aborts host start
by throwing when errors (`ThrowOnError`) or errors-and-warnings (`ThrowOnWarning`) are present. `WarnOnly`
logs everything but never aborts; `Off` skips validation entirely.

The global checks (`CQRCONF001`–`004`) are produced by the public `CqrsConfigurationInspector`, which
the source-generated diagnostics class calls; the marker checks (`CQRCONF005`/`006`) detect a request
opting into idempotency/retries via a marker whose behavior was never wired, using internal behavior
markers so `CQRSharp.Core` takes no dependency on the concrete behavior types. A behavior that is
registered but **exempted** for a request is not flagged.

## The introspection API

`ICqrsDiagnostics` is a scoped service (registered by `AddCqrsGenerated`) that describes exactly how
each request is bound in the current scope:

```csharp
public interface ICqrsDiagnostics
{
    bool TryDescribeRequest(Type requestType, out CqrsRequestBinding binding);
    CqrsRequestBinding DescribeRequest(Type requestType);
    IReadOnlyList<CqrsRequestBinding> DescribeAllRequests();
    IReadOnlyList<CqrsBindingIssue> DescribeConfiguration();
}
```

A `CqrsRequestBinding` is an immutable snapshot of one request's wiring:

```csharp
public sealed record CqrsRequestBinding(
    Type RequestType, Type ResponseType,
    Type? HandlerType, Type? ContextType,
    IReadOnlyList<Type> PipelineExemptions,
    IReadOnlyList<CqrsInterceptorBinding> PreHandlers,
    IReadOnlyList<CqrsInterceptorBinding> PostHandlers,
    IReadOnlyList<CqrsPipelineBehaviorBinding> Pipeline,         // active behaviors, in order
    IReadOnlyList<CqrsPipelineBehaviorBinding> ExemptedPipeline, // skipped because exempted
    IReadOnlyList<CqrsBindingIssue> Issues);
```

```csharp
var diag = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>();
foreach (var b in diag.DescribeAllRequests())
    Console.WriteLine($"{b.RequestType.Name} -> {b.HandlerType?.Name} " +
                      $"[{string.Join(", ", b.Pipeline.Select(p => p.BehaviorType.Name))}]");
```

Use it to dump the resolved pipeline at startup, build a `/cqrs/bindings` debug endpoint, or assert
wiring in tests. `DescribeConfiguration()` returns the same global `CQRCONF` issues the validator checks.

## Binding issues (CQRDIAG)

Each `CqrsRequestBinding` carries an `Issues` list of `CqrsBindingIssue` records — per-request problems
detected while the **source-generated** diagnostics describe one request's wiring (and surfaced through
the [introspection API](#the-introspection-api) and the [health check](#health-checks) below). They use
the `CQRDIAG` prefix, are all `Error` severity, and signal that the request would not dispatch correctly:

| ID | Condition | Remedy |
| --- | --- | --- |
| `CQRDIAG001` | No `RequestMetadata` is registered for the request type. | Ensure the request (and its handler) is in an assembly where the generator runs, so it's included in the generated registry. |
| `CQRDIAG002` | The request's metadata has no handler type — no handler was wired for it. | Declare exactly one discoverable handler for the request (see `CQRA003` / `CQRGEN003`). |
| `CQRDIAG003` | The context factory for the request's context type couldn't be resolved — either none is registered, or resolving it threw. | Register an `IRequestContextFactory<TContext>` for the context type (or use the default context); see `CQRA011`. |
| `CQRDIAG004` | Resolving the pipeline behaviors (request or stream) for the request threw. | Fix the failing behavior registration / its dependencies so the behaviors resolve. |

These are produced by `GeneratedCqrsDiagnostics` when you call `DescribeRequest` / `DescribeAllRequests`,
so you'll see them on a binding's `Issues` (and in the health-check report) rather than at compile time.

## Health checks

Register a health check that validates all CQRSharp bindings on the standard ASP.NET Core health-checks
pipeline:

```csharp
services.AddHealthChecks()
    .AddCqrsBindings();   // name: "cqrsharp.bindings", failureStatus: Unhealthy
```

`AddCqrsBindings(name = "cqrsharp.bindings", failureStatus = Unhealthy, tags = null)` registers
`CqrsBindingsHealthCheck`, which reports unhealthy when any request binding has issues — a live signal
that complements the one-shot startup validator.
