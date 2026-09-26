# Diagnostics &amp; validation

CQRSharp catches mistakes at four moments: **as you type** (Roslyn analyzers), **at build** (generator diagnostics),
**at host start** (the startup validator, on by default in the Development environment) and **at first use** (the
first dispatch of a request, the first publish of a notification, in every environment). A runtime **introspection
API** shows how every request is wired, and an **outbox health check** reports the outbox's backlog.

- [Compile-time analyzers (CQRA)](#compile-time-analyzers-cqra)
- [Generator diagnostics (CQRGEN)](#generator-diagnostics-cqrgen)
- [Startup validation (CQRCONF)](#startup-validation-cqrconf)
- [First-use checks](#first-use-checks)
- [The introspection API](#the-introspection-api)
- [Binding issues (CQRDIAG)](#binding-issues-cqrdiag)
- [Health checks](#health-checks)
- [Removed diagnostics](#removed-diagnostics)

Every id is stable: a removed id is never reused.

## Compile-time analyzers (CQRA)

The analyzers run in the editor and on build, in the `CQRSharp.Usage` category. They ship in `CQRSharp.Abstractions`,
so every project that references any CQRSharp package gets them.

| ID | Severity | What it reports |
| --- | --- | --- |
| `CQRA003` | Warning | A request dispatched with `Send`/`Stream` has no discoverable handler in this compilation or its references, so dispatching it fails at runtime unless a handler is registered another way. |
| `CQRA004` | Error | A stream request is dispatched with `Send(...)`. The C# compiler accepts it (an `IStreamRequest<T>` is also an `IRequest<IAsyncEnumerable<T>>`), but the dispatcher throws at runtime. **Code fix:** rewrites `await d.Send(r)` to `d.Stream(r)`; not offered when the `Send` task is used as a value. |
| `CQRA005` | Error | `[PipelineExemption(typeof(X))]` has no effect: `X` is not a pipeline behavior, is abstract or an interface, never runs for the request (closed over another request or result, or of the other pipeline kind, such as a stream behavior on a command), or the attribute sits on a type that is not a request. The exemption is inherited, as the generator applies it: on a class other requests can derive from, it is not reported while the behavior runs for such a derived request. |
| `CQRA006` | Info | A published notification has no discoverable handler; the publish does nothing unless a handler is registered another way. Legal, but often unintended. |
| `CQRA008` | Info | A `[PipelineExemption]` names the closed form of a generic behavior; the open-generic `typeof(Behavior<,>)` exempts the same behavior for this request. Offered only on a request no other request can derive from (a sealed class or a value type): on any other, the open form would also exempt the behavior for every derived request. **Code fix:** converts it. |
| `CQRA010` | Warning | A project declares CQRSharp handlers but the source generator does not run in it, so no module is emitted and its handlers are never registered. Reference the `CQRSharp` package from the project. Also reported in a project that references only `CQRSharp.Abstractions`. |
| `CQRA011` | Warning | A request declares a custom context (`CommandBase<TContext>`, `QueryBase<TResult, TContext>`, `ResultCommandBase<TResult, TContext>` or `StreamRequestBase<TItem, TContext>`) but no `IRequestContextFactory<TContext>` is discoverable, so its first dispatch throws. Declare a factory (the generator registers any it can see) or use the default context. |
| `CQRA012` | Warning | An `IRequestValidator<T>` is declared, but no CQRSharp registration in the project adds the validation behavior, so input reaches the handler **unvalidated**. `AddCqrsGenerated()` and `AddCqrsGenerated(b => ...)` add it unless `UseValidation(false)` turns it off; only an opt-out that always runs counts (not one under a condition, in a loop, a local function or a nested lambda). |
| `CQRA014` | Error | `AddCqrs()` is called directly: it registers the dispatcher but not the generated routing, so the first `Send`/`Stream`/`Publish` throws. **Code fix:** calls `AddCqrsGenerated(...)` instead; withheld when the rewritten call would bind a different overload. |
| `CQRA015` | Warning | A query, stream or plain request implements `ITransactionalCommand`, which opts a command into a unit-of-work transaction but does not make a request a command: it commits, but keeps its query or stream lifecycle notifications, span and metrics. Make it a command (`ICommand` / `ICommand<TResult>`), or mark a query or stream that writes with `ITransactionalQuery` and `IsReadOnly = false`. |
| `CQRA018` | Error | The application handles a request that implements `IIdempotentRequest`, and its CQRSharp configuration never calls `UseIdempotency(...)`, so every dispatch of it fails at runtime with `CQRCONF005`. Reported at the `AddCqrsGenerated` call, only for a configuration [in view](#configuration-in-view). **Code fix:** adds `UseIdempotency()` to the configuration. |
| `CQRA019` | Warning | The application handles a request that implements `IRetryableRequest`, and its CQRSharp configuration never calls `UseResilience(...)`, so its failures are not retried (`CQRCONF006` at runtime). Reported at the `AddCqrsGenerated` call, only for a configuration [in view](#configuration-in-view). **Code fix:** adds `UseResilience(o => o.MaxRetries = 3)`. |
| `CQRA020` | Info | A configuration [in view](#configuration-in-view) turns the outbox on (`UseOutbox`) under the generated serializer, and a notification class declared in the project has handlers but no `[NotificationName]`, so it is never stored in the outbox (`CQRCONF003` at runtime). It may be meant to stay in-process, hence a suggestion. Not reported where code in view names `INotificationSerializer` (a type implementing it, `AddNotificationSerializer<T>()`) or `OutboxOptions`. **Code fix:** adds `[NotificationName("...")]` with a name in the lower-case dotted form (`OrderPlacedNotification` becomes `order.placed`, or `shop.orders.order.placed` when that is taken); a fixed name, since renaming the type later must not change it. |

`CQRA003`, `CQRA006` and `CQRA011` work across projects: the generator emits assembly-level markers for every handled
request, handled notification and registered context factory, and a project sees the markers of the projects it
references. So `CQRA011` cannot see a factory declared in a project that references the request's project (a factory in
`Infrastructure` for a request in `Application`); suppress it there. `CQRA012` fires only in a project that registers
CQRSharp, and recognizes the registration and `UseValidation(false)` by the methods they bind to.

### Configuration in view

`CQRA018`, `CQRA019` and `CQRA020` report a builder verb as missing, which is only true when nothing else can supply
it, so they report nothing unless the application's whole CQRSharp configuration is in view:

- The project is an application (an executable). A library is never reported: the application that references it may
  configure CQRSharp further.
- The project calls one CQRSharp registration, once: `AddCqrsGenerated()`, or `AddCqrsGenerated(b => ...)` with a lambda
  written in place (not a variable or a method group) that is a chain of builder verbs on its parameter, as one
  expression or as statements. No condition, loop, local function, helper method or builder extension of the project's
  own (CQRSharp's `UseFluentValidation` and `UseEntityFrameworkCoreUnitOfWork` are known); no verb argument that reaches
  the service collection or the builder or calls the project's own code while it configures (a factory the container
  calls later, `sp => ...`, is fine).
- No referenced assembly other than CQRSharp's packages can configure CQRSharp: none references `CQRSharp.Core` or
  `CQRSharp.Pipelines` (a module with handlers, a library that wires behaviors), none references
  `CQRSharp.Abstractions` together with the dependency-injection abstractions (it could register a serializer), and no
  assembly-scanning registration library (Scrutor) is referenced. A contracts project that references
  `CQRSharp.Abstractions` alone is fine.
- No code in the project names a built-in behavior a verb registers (`IdempotencyBehavior<,>`, `ResilienceBehavior<,>`
  and their stream forms), or `typeof(IPipelineBehavior<,>)` / `typeof(IStreamPipelineBehavior<,>)` other than in a plain
  container registration of the application's own behavior (`services.AddTransient(typeof(IPipelineBehavior<,>),
  typeof(MyBehavior<,>))`), since a scan could register the built-in ones.

Registrations made by reflection over type names are beyond any analyzer; if an application registers CQRSharp's
behaviors that way, suppress the diagnostic.

## Generator diagnostics (CQRGEN)

The source generator reports these during compilation, in the `CQRSharp.Generators` category. A project that does not
reference `CQRSharp.Core` gets no generated code and no generator diagnostic.

| ID | Severity | Meaning |
| --- | --- | --- |
| `CQRGEN002` | Error | Two notification types in the assembly carry the same `[NotificationName]`. Stable names must be unique for outbox serialization. |
| `CQRGEN003` | Warning | A request declared in the project has no handler in it. Add one, or, in a project that only declares requests whose handlers live elsewhere, suppress it (`<NoWarn>$(NoWarn);CQRGEN003</NoWarn>` or `dotnet_diagnostic.CQRGEN003.severity = none`). |
| `CQRGEN004` | Error | Several handlers for one request. CQRSharp requires exactly one. |
| `CQRGEN005` | Error | A `[NotificationName]` notification cannot be serialized by the generated outbox serializer; the message names the reason. Change its shape (see [supported shapes](outbox.md#supported-shapes)), or remove `[NotificationName]` and register a serializer of your own with `AddNotificationSerializer<T>()`, which replaces the generated one and must then name and serialize every durable notification. |
| `CQRGEN006` | Warning | A type implements a CQRSharp handler interface but generated code cannot name it: it is less accessible than `internal`, or marked `[Obsolete]` as an error (CS0619, which no pragma suppresses), so generated registration skips it. Make it `public` or `internal` (and not nested in a less accessible type) without an error-level `[Obsolete]`, or register it by hand. A plain `[Obsolete]` or `[Experimental]` type is fine: generated files suppress those warnings for the types they name. |
| `CQRGEN007` | Error | A CQRSharp framework type could not be resolved although `CQRSharp.Core` is referenced, so generation is skipped. Reference the same version of every CQRSharp package. |
| `CQRGEN009` | Warning | An open-generic handler (`Handler<T>`) is never registered: only closed, non-generic handler types are. Declare a closed handler or register it by hand. |
| `CQRGEN010` | Warning | A binding is not wired because a type it names is not accessible to generated code (less accessible than `internal`, internal to another assembly without `InternalsVisibleTo`, or marked `[Obsolete]` as an error): the request, its result or item type, a notification, a validator's or exception hook's request, or a closed pipeline behavior's type argument. The request is not routed, or the binding never runs. Make the type `public` or `internal`. |
| `CQRGEN011` | Error | `[NotificationName(PartitionBy = "...")]` names no readable instance property generated code can see, so the notification's outbox deliveries cannot be ordered. Name a public or internal property, or implement `IPartitionedNotification`. |
| `CQRGEN012` | Error | Two notification handlers in the assembly carry the same stable handler name. Outbox messages are addressed to a handler by name, so give each a unique `[NotificationHandlerName]`. |
| `CQRGEN013` | Warning | A `[NotificationHandlerName]` is empty or whitespace; the handler's type name is used instead. |
| `CQRGEN014` | Info | An `IIdempotentRequest`'s properties cannot be rendered for the automatic payload fingerprint, so a key reused with a different payload is not detected for it. Implement `IFingerprintedRequest`, or leave properties that are not payload out with an unconditional `[JsonIgnore]`. Reported at the request, or at the handler of a request declared in another assembly. |
| `CQRGEN015` | Warning | A referenced assembly exposes its internal `AddCqrsGenerated` to this one (`InternalsVisibleTo`, typically an app and its test project), so a plain `services.AddCqrsGenerated()` here is ambiguous (CS0121) or binds to that assembly's copy and skips this assembly's own module. Reported at each such call; call `CQRSharp.Generated.<AssemblyName>.CqrsGeneratedBootstrap.AddCqrsGenerated(services)` instead. Reported as information, with no call to fix, when the plain call is right. |
| `CQRGEN016` | Error | A pre-/post-handler attribute or a `[PipelineExemption]` on a request (or inherited from a base class) names a type generated code cannot name: a private, protected or file-local type, or one internal to another assembly without `InternalsVisibleTo`. The request would be dispatched without it. Make the types public or internal. |
| `CQRGEN017` | Error | A handler is declared over a response type that is not the one its request is dispatched with, so the dispatcher never calls it: an `IRequestExceptionHandler<TRequest, TResponse, TException>` whose `TResponse` is not exactly `CommandResult`, `CommandResult<T>`, the query result or `IAsyncEnumerable<T>`, or a request handler declared over a wider result through covariance. |
| `CQRGEN018` | Error | Two `IRequestContextFactory` classes in the assembly serve the same context type. A context type has one factory; keep one. |
| `CQRGEN019` | Warning | Two or more referenced assemblies each register a context factory for one context type, and this assembly, which composes them, declares none, so module order decides which one runs. Reported at each `AddCqrsGenerated` call. Declare an `IRequestContextFactory<T>` in this assembly (it replaces theirs) or keep only one. A factory registered by hand also resolves it; suppress the warning then. |
| `CQRGEN020` | Warning | The assemblies this one composes (itself and every referenced module) give one `[NotificationName]` to more than one notification type across assemblies. While the outbox is on, this assembly's type keeps the name (or, between two referenced modules, the one registered last) and a publish of any other through the outbox fails with `CQRCONF010`. Reported at each `AddCqrsGenerated` call; a warning, since whether the outbox will be on is not known at build. Give each a unique `[NotificationName]`. |
| `CQRGEN021` | Warning | The assemblies this one composes use one notification handler name for more than one handler across assemblies (the same namespace and type name, or the same `[NotificationHandlerName]`), so while the outbox is on a message addressed to it is ambiguous (`CQRCONF009`). Reported at each `AddCqrsGenerated` call. Give each a unique `[NotificationHandlerName]`. |
| `CQRGEN999` | Error | An unhandled exception in the generator. Please report it. |

## Startup validation (CQRCONF)

The startup validator inspects the configuration and every request binding **once, before any hosted service starts**,
whatever the registration order, so a seeder or a web server never runs against a configuration it rejects. Unless a
policy is set it runs in the **Development** environment only, as `ThrowOnError`, the way the host validates its
container there; everywhere else it is off unless `ValidateOnStart()` turns it on (see
[Configuration](configuration.md#startup-validation)). It resolves every pipeline behavior of every request, which is
the cold cost each request type otherwise pays at its first dispatch, paid for all of them before the host serves
anything: some 15 ms for the 18 requests of the Native AOT sample, and 100 to 230 ms for an application of 300 requests
(JIT and Native AOT). The rules that matter most do not wait for it: they are also checked at
[first use](#first-use-checks), in every environment.

| ID | Severity | Condition |
| --- | --- | --- |
| `CQRCONF001` | Error | An outbox mode is enabled, but a required outbox service (`IOutboxStore`, `INotificationSerializer`, or the notification subscription registry `AddCqrsGenerated` registers) is not registered, so the processor never delivers anything. |
| `CQRCONF003` | Warning | An outbox mode is enabled and a concrete notification type that has handlers (including through a base-type or interface handler) gets no name from the registered serializer, so it is dispatched in-process instead of through the outbox. The remedy depends on the serializer: `[NotificationName]` for the generated one; the custom serializer's `TryGetNotificationName` otherwise. Not reported, under the generated serializer, for a `struct` notification or for CQRSharp's own lifecycle notifications, which stay in-process by design. |
| `CQRCONF004` | Error | `ICqrsDiagnostics` is not registered, so the generated registrations were not applied: `AddCqrs()` was called instead of `AddCqrsGenerated()`. |
| `CQRCONF005` | Error | A request implements `IIdempotentRequest` but no idempotency behavior is registered, so duplicates are processed. Call `UseIdempotency(...)`. |
| `CQRCONF006` | Warning | A request implements `IRetryableRequest` but no resilience behavior is registered, so failures are not retried. Call `UseResilience(...)`. |
| `CQRCONF007` | Error | The outbox mode is `Transactional` but no `IUnitOfWork` is registered, so there is never a transaction and nothing reaches the outbox. Register a unit of work, or use the `Enabled` mode. |
| `CQRCONF009` | Error | An outbox mode is enabled and two handler **types** share one stable handler name (across assemblies), so a message addressed to it is ambiguous. Give each a unique `[NotificationHandlerName]`. |
| `CQRCONF010` | Error | An outbox mode is enabled and two modules give different notification types the same `[NotificationName]`, so a stored message could not be read back as the type it was stored as. The composition root's type keeps the name; the other is not durable. |
| `CQRCONF011` | Warning | An outbox mode is enabled and handlers registered by hand exist for a durable notification. They have no outbox subscription, so a publish that goes through the outbox never reaches them. Declare them where the source generator runs. |
| `CQRCONF012` | Error | An outbox mode is enabled and the handlers registered by hand for a notification cannot be constructed (a missing dependency), so the `CQRCONF011` check cannot tell them apart. Publishing the notification in-process fails the same way. Reported instead of aborting host start, so `WarnOnly` still starts. |

`CQRCONF005` and `CQRCONF006` are not reported for a request whose behaviors could not be resolved (`CQRDIAG004`), nor
when the behavior is registered but the request is exempted from it. Every check asks the container whether a service
is registered rather than building it, so validation opens no connection.

The validator logs every issue with its code (event 1200 for an error, 1201 for a warning), then a summary (1203), or a
pass (1202). What happens next follows the policy:

| Policy | Selected with | Effect |
| --- | --- | --- |
| `Off` | no policy set outside Development, or `ValidateOnStart(false)` | Validation is skipped. |
| `WarnOnly` | `ValidateOnStart(CqrsValidationPolicy.WarnOnly)` | Every issue is logged; host start always proceeds. |
| `ThrowOnError` | no policy set in Development, or `ValidateOnStart()` | Host start is aborted with an `InvalidOperationException` listing the errors. |
| `ThrowOnWarning` | `ValidateOnStart(CqrsValidationPolicy.ThrowOnWarning)` | Host start is aborted when there is any error or warning. |

The policy is `CqrsStartupValidationOptions.Policy` (namespace `CQRSharp`), a `CqrsValidationPolicy?`, so it can also
be set with `services.Configure<CqrsStartupValidationOptions>(...)` or bound from configuration. Left `null` (the
default), the host environment decides: `ThrowOnError` when the `IHostEnvironment` is Development, `Off` in any other
environment and when there is no host environment at all. A policy that is set applies in every environment. An
undefined value fails host start.

## First-use checks

Where a rule has a natural first point of use, the runtime checks it there too, in every environment and whether or
not the startup validator runs, so a misconfiguration never degrades silently in production. Each check runs once per
service provider and type, as part of the plan the dispatcher or publisher builds for that type anyway: a correctly
wired request or notification pays nothing more than its cached plan (a request without markers has no check at all).
The rule and its message are the startup validator's own, so a code means the same wherever it is reported. An error
is kept with the plan and fails **every** dispatch or publish of that type with an `InvalidOperationException` whose
message starts with `CQRSharp configuration error <code>:`; a warning is logged once, under the category
`CQRSharp.Core.Diagnostics.CqrsConfiguration` (event 1204 for a request, 1205 for a notification; 1206 for an error that
does not fail the publish).

| ID | At first use | Why |
| --- | --- | --- |
| `CQRCONF001` | The first publish that goes to the outbox fails. | Nothing could store it. (It already failed there; the message now carries the code.) |
| `CQRCONF003` | The first publish under an outbox mode of a handled notification the serializer does not name logs a warning; the notification is delivered in-process, as before. | It may be meant to stay in-process. |
| `CQRCONF004` | No separate check: without the generated registrations the first `Send`, `Stream` or `Publish` already fails for want of a route, and `CQRA014` makes the direct `AddCqrs()` call a build error. | |
| `CQRCONF005` | The dispatch of an `IIdempotentRequest` fails, every time, unless the idempotency behavior is registered for it (exempting a registered one is an opt-out, not a gap), before its handler runs. | Running it would process duplicates. Checked against the behaviors the dispatch resolves anyway, so it costs no extra resolution. |
| `CQRCONF006` | The first dispatch of an `IRetryableRequest` without the resilience behavior logs a warning; the request runs. | It only forgoes retries: failing it would turn a missing resilience layer into an outage. |
| `CQRCONF007` | Under `Transactional` mode with no `IUnitOfWork`, a publish of a notification the serializer names fails, every time; any other is delivered in-process as before. | It was meant to be durable, and never can be. |
| `CQRCONF009` | No runtime check: a handler name clash between modules is reported at build as `CQRGEN021` in the assembly that composes them, and the subscription tables only come from generated modules, which the generator composes. The startup validator still reports it. | |
| `CQRCONF010` | A publish that would go to the outbox of a handled notification that lost its `[NotificationName]` to another module's type fails, every time. Also reported at build as `CQRGEN020`. | Its declared durability cannot be honored. |
| `CQRCONF011` | The first publish that goes to the outbox of a durable notification with handlers registered by hand logs a warning; delivery is unchanged. | The handlers still run when it is dispatched in-process. |
| `CQRCONF012` | The same first publish logs an error when those handlers cannot be constructed; the publish, which constructs none of them, is not failed. | An in-process publish of the type fails with the same exception. |

## The introspection API

`ICqrsDiagnostics` (namespace `CQRSharp.Core.Diagnostics`) is a scoped service, registered by `AddCqrsGenerated`, that
describes how each request is bound in the current scope and runs the same configuration checks as the startup
validator, on demand:

```csharp
public interface ICqrsDiagnostics
{
    bool TryDescribeRequest(Type requestType, [NotNullWhen(true)] out CqrsRequestBinding? binding);
    CqrsRequestBinding DescribeRequest(Type requestType);   // throws InvalidOperationException for an unknown type
    IReadOnlyList<CqrsRequestBinding> DescribeAllRequests();
    IReadOnlyList<CqrsBindingIssue> DescribeConfiguration();
}
```

`DescribeAllRequests` covers every request the application dispatches, closed generic requests and requests declared
in contracts-only assemblies included, ordered by type name; a request two modules handle is described once, with the
handler that serves it. `DescribeConfiguration` returns the `CQRCONF` issues.

A `CqrsRequestBinding` is an immutable snapshot of one request's wiring:

```csharp
public sealed record CqrsRequestBinding(
    Type RequestType, Type ResponseType,
    Type? HandlerType, Type? ContextType,
    IReadOnlyList<Type> PipelineExemptions,
    IReadOnlyList<CqrsInterceptorBinding> PreHandlers,             // in execution order
    IReadOnlyList<CqrsInterceptorBinding> PostHandlers,            // in execution order
    IReadOnlyList<CqrsPipelineBehaviorBinding> Pipeline,           // the behaviors that run, in execution order
    IReadOnlyList<CqrsPipelineBehaviorBinding> ExemptedPipeline,   // registered, but skipped because exempted
    IReadOnlyList<CqrsBindingIssue> Issues);
```

A `CqrsPipelineBehaviorBinding` is `(Type BehaviorType, int Priority)`, a `CqrsInterceptorBinding` is
`(Type AttributeType, int Priority)`, and a `CqrsBindingIssue` is `(CqrsBindingIssueSeverity Severity, string Code,
string Message)` with a severity of `Warning` or `Error`.

```csharp
using CQRSharp.Core.Diagnostics;

var diag = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>();
foreach (var b in diag.DescribeAllRequests())
    Console.WriteLine($"{b.RequestType.Name} -> {b.HandlerType?.Name} " +
                      $"[{string.Join(", ", b.Pipeline.Select(p => p.BehaviorType.Name))}]");
```

Use it to print the resolved pipeline at startup, build a debug endpoint, or assert wiring in tests.

## Binding issues (CQRDIAG)

Each `CqrsRequestBinding` carries an `Issues` list: problems found while describing that request, reported at runtime by
the introspection API and by the startup validator. An error means the request would not dispatch correctly; a warning
means something declared for it never runs:

| ID | Severity | Condition | Remedy |
| --- | --- | --- | --- |
| `CQRDIAG001` | Error | No request metadata is registered for the request type. | Make sure the request's handler is in an assembly the generator runs in. |
| `CQRDIAG003` | Error | The context factory for the request's context type is not registered, or resolving it threw. | Register an `IRequestContextFactory<TContext>` for the context type, or use the default context (see `CQRA011`). |
| `CQRDIAG004` | Error | Resolving the request's pipeline behaviors (or stream pipeline behaviors) threw. This includes, under Native AOT, a registered open-generic behavior that generated code could not close for a value-type-result request. | Fix the failing behavior registration or its dependencies. |
| `CQRDIAG005` | Warning | The request has validators (declared in any assembly), but the validation behavior is not in its pipeline, so they never run and its input reaches the handler unvalidated. Complements `CQRA012`, which sees only the validators of its own project. | Register CQRSharp without `UseValidation(false)`. |
| `CQRDIAG006` | Warning | The request has exception actions or handlers, but the exception-handling behavior is not in its pipeline, so they never run. | Register CQRSharp without `UseExceptionHandling(false)`. |

## Health checks

`AddCqrsOutbox()` (namespace `CQRSharp.Core.Diagnostics.HealthChecks`) registers a health check that measures the
outbox backlog on every probe:

```csharp
using CQRSharp.Core.Diagnostics.HealthChecks;

services.AddHealthChecks().AddCqrsOutbox();   // name "cqrsharp.outbox"
```

Its thresholds, statuses and data are described in
[The outbox](outbox.md#backlog-gauges-and-the-health-check). Configuration is not a health signal: use the startup
validator for it.

## Removed diagnostics

These ids were reported by earlier releases and are retired; a `#pragma` or `NoWarn` entry for them is harmless.

| ID | Why it is gone |
| --- | --- |
| `CQRA001` | Handler interfaces take no context type argument, so a handler cannot mismatch its request's context. |
| `CQRA007` | Rate limiting is opt-in per request, so a request without `IRateLimitedContext` is not a mistake. |
| `CQRA009` | It fired on every value-returning command, the correct ones included; the guidance lives on `ICommand<TResult>`. |
| `CQRA013` | Retries and idempotency are opt-in per request; `CQRCONF005` / `CQRCONF006` (and, at build, `CQRA018` / `CQRA019`) report a marker whose behavior is not wired. |
| `CQRA017` | `ICommandInterceptor` is removed; an attribute implements `IPreHandlerAttribute` and `IPostHandlerAttribute`. |
| `CQRGEN008` | A project without `CQRSharp.Core` gets no generated code, and needs no diagnostic to say so. |
| `CQRCONF002` | A unit of work always reports whether a transaction is active (`IUnitOfWork.HasActiveTransaction`), so the transactional outbox can always detect one. |
| `CQRCONF008` | Streams run inline under `RunMode.Queued`, so a stream request is valid in a queued application. |
| `CQRDIAG002` | Every request binding carries its handler type, so a request without one is already `CQRDIAG001`. |
