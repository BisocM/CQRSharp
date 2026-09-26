# Migrating from MediatR

This guide takes a codebase on **MediatR 12.x** (the last Apache-2.0 line; MediatR 13 and later are commercially
licensed) to **CQRSharp 5.0**. It maps each MediatR concept to its CQRSharp counterpart, shows the code before and after,
and describes a migration you can do one feature at a time, with both libraries in one container until the last MediatR
handler is gone. The code in this guide was compiled and run against MediatR 12.5.0 and the CQRSharp 5.0.0 packages.

- [Should you migrate?](#should-you-migrate)
- [Concept map](#concept-map)
- [The migration, step by step](#the-migration-step-by-step)
- [Requests and handlers](#requests-and-handlers)
- [Notifications](#notifications)
- [Pipeline behaviors](#pipeline-behaviors)
- [Pre- and post-processors](#pre--and-post-processors)
- [Exception handlers and actions](#exception-handlers-and-actions)
- [Porting common behaviors to the built-ins](#porting-common-behaviors-to-the-built-ins)
- [Lifetimes, scopes and cancellation](#lifetimes-scopes-and-cancellation)
- [Tests](#tests)
- [ASP.NET Core](#aspnet-core)
- [Pitfalls checklist](#pitfalls-checklist)
- [FAQ](#faq)

## Should you migrate?

The two libraries solve the same core problem, in-process dispatch of requests and notifications through a pipeline,
in different ways. The [comparison in the README](../README.md#cqrsharp-and-mediatr) lists the differences feature by
feature; this section is about what a migration buys you and what it costs.

**What you gain**

- **Wiring checked at build.** A source generator finds your handlers and writes the dispatch code, so a request with no
  handler, two handlers for one request, a stream sent with `Send` or a handler the generator cannot see is a diagnostic
  in your IDE ([Diagnostics](diagnostics.md)), not an exception at the first call in production. MediatR finds handlers
  by scanning assemblies with reflection when the container is built.
- **Native AOT and trimming.** No `MakeGenericType`, no `Activator`, no assembly scanning; the core packages build clean
  under the AOT analyzers ([Native AOT](native-aot.md)). MediatR's dispatcher builds its handler wrappers with
  `MakeGenericType` at runtime.
- **The behaviors you would otherwise write.** Logging, validation (native or FluentValidation), retries, timeouts, rate
  limiting, idempotency with result replay, a unit of work and a transactional outbox are one builder call each, in a
  fixed and documented order ([Pipeline behaviors](pipeline-behaviors.md)).
- **Commands and queries as distinct kinds**, with `CommandResult` for expected failures, and lifecycle notifications
  around every request ([Notifications](notifications.md#lifecycle-notifications)).
- **Startup validation and first-use checks** that report wiring mistakes with stable codes
  ([Diagnostics](diagnostics.md#startup-validation-cqrconf)).
- **Test and HTTP support**: `RecordingCqrsDispatcher` for unit tests, and `CommandResult` and pipeline exceptions mapped
  to ProblemDetails for ASP.NET Core.
- **An MIT license** for every future version.
- **Dispatch cost.** [benchmarks/README.md](../benchmarks/README.md#latest-results) measures both libraries at their
  defaults. Resolved once from a long-lived scope, CQRSharp dispatches every measured scenario faster and allocates
  less; a notification costs about half of MediatR's. When every dispatch also creates a new DI scope and resolves the
  scoped `ICqrsDispatcher`, a notification costs about what MediatR's does and a stream less, while a request is about a
  third slower. Compare the ratios there, not absolute times.

**What changes, or what you give up**

- **The MediatR ecosystem.** Libraries and templates that integrate with `IMediator` (behavior packages, source-generated
  endpoint mappers, samples) do not work with `ICqrsDispatcher`.
- **Runtime discovery.** CQRSharp wires what the generator sees at compile time: handlers in the calling assembly and in
  the CQRSharp assemblies it references. A plugin assembly loaded at runtime, a `private` nested handler, or a type
  filter such as MediatR's `TypeEvaluator` has no equivalent.
- **Open-generic flexibility.** Open-generic *behaviors* work as in MediatR. Open-generic *handlers* (MediatR's
  `RegisterGenericHandlers`), open-generic notification handlers and open-generic exception hooks are not registered;
  see [the FAQ](#can-handlers-be-generic).
- **Requests are classes.** Requests derive from CQRSharp's base classes (`CommandBase`, `QueryBase<T>`, ...), so a
  request declared as a `record` becomes a class. Notifications can stay records.
- **Failures are values.** A command reports an expected failure by returning a failed `CommandResult`; `Send` does not
  throw for it. Code that relied on exceptions for "not found" or "conflict" changes shape.
- **One pipeline order, by priority.** Behaviors are ordered by priority, not by registration order, and the built-ins
  have fixed places ([Execution order](pipeline-behaviors.md#execution-order)).
- **A scoped dispatcher.** `ICqrsDispatcher` is scoped. MediatR registers `IMediator` as transient by default, so it can
  be injected into a singleton; `ICqrsDispatcher` cannot (see
  [Lifetimes, scopes and cancellation](#lifetimes-scopes-and-cancellation)).
- **No pluggable notification publisher.** The publish strategy is an option with three values; a custom
  `INotificationPublisher` has no equivalent.

**What has no equivalent.** These MediatR features have no CQRSharp counterpart; the right-hand column is what to
do instead:

| MediatR | Instead |
| --- | --- |
| Assembly scanning at runtime, `TypeEvaluator`, handlers in assemblies loaded at runtime | Reference the handler projects from the host; the generator composes them. |
| `private` nested handlers | Make them `internal` (**CQRGEN006** reports them). |
| `RegisterGenericHandlers`, open-generic request and notification handlers | Closed handlers, which may derive from a generic base class. |
| Exception handlers for a base request type or open-generic ones | One hook per request type, or a behavior. |
| `RequestExceptionActionProcessorStrategy.ApplyForUnhandledExceptions` | Actions always run; check in the action whether to act. |
| A custom `INotificationPublisher` | `PublishStrategy`, notification behaviors, the outbox. |
| `NotificationHandler<T>` (synchronous base class) | Implement `INotificationHandler<T>` and return `Task.CompletedTask`. |
| `cfg.Lifetime`, `MediatorImplementationType` (a custom mediator) | The dispatcher is always scoped; to add behavior around dispatch, use a pipeline behavior or wrap `ICqrsDispatcher` in a service of your own. |
| `IPublisher.Publish(object)` | `Publish((INotification)message)`. |
| Separate `ISender` and `IPublisher` interfaces | `ICqrsDispatcher` for both. |

If your application depends on runtime discovery or open-generic handlers throughout, weigh that first: it is the part
of a migration that changes the most code.

## Concept map

| MediatR 12.x | CQRSharp 5.0 | Notes |
| --- | --- | --- |
| `IRequest<TResponse>` that changes state | `ResultCommandBase<TResult>` (`ICommand<TResult>`), returning `CommandResult<TResult>`; or `CommandBase` and a query to read the result | [Commands with a result](#commands-with-a-result) |
| `IRequest` (no response), `Unit` | `CommandBase` (`ICommand`), returning `CommandResult` | [Commands without a result](#commands-without-a-result) |
| `IRequest<TResponse>` that only reads | `QueryBase<TResult>` (`IQuery<TResult>`) | [Queries](#queries) |
| `IRequestHandler<TRequest, TResponse>` | `IResultCommandHandler<TCommand, TResult>` or `IQueryHandler<TQuery, TResult>` | One handler per request, enforced at build (**CQRGEN004**). |
| `IRequestHandler<TRequest>` | `ICommandHandler<TCommand>` | |
| `IStreamRequest<T>`, `IStreamRequestHandler<TRequest, T>` | `StreamRequestBase<TItem>`, `IStreamRequestHandler<TRequest, TItem>` | [Streams](#streams) |
| `INotification`, `INotificationHandler<T>` | `INotification`, `INotificationHandler<T>` (namespace `CQRSharp`) | Same shape. [Notifications](#notifications) |
| `NotificationHandler<T>` (synchronous base class) | none | Implement `INotificationHandler<T>`. |
| `IMediator`, `ISender`, `IPublisher` | `ICqrsDispatcher` | Scoped. |
| `Send(request)`, `Send(object)` | `Send(request)`, `Send(object)` | |
| `CreateStream(request)`, `CreateStream(object)` | `Stream(request)`, `Stream(object)` | `Send` with a stream request is **CQRA004**. |
| `Publish(notification)`, `Publish(object)` | `Publish(notification)` | No `object` overload: cast to `INotification`. |
| `AddMediatR(cfg => cfg.RegisterServicesFromAssembly...(...))` | `AddCqrsGenerated()` or `AddCqrsGenerated(b => ...)` | Generated; no assemblies to name. |
| `IPipelineBehavior<TRequest, TResponse>` | `IPipelineBehavior<TRequest, TResult>` (namespace `CQRSharp.Pipelines`) | Constraint `where TRequest : IRequest`. [Pipeline behaviors](#pipeline-behaviors) |
| `cfg.AddOpenBehavior(typeof(B<,>))` | `services.AddTransient(typeof(IPipelineBehavior<,>), typeof(B<,>))` | Closed behaviors are discovered by the generator. |
| Behavior order = registration order | Behavior order = priority (`IPrioritizedPipelineBehavior`) | |
| `IStreamPipelineBehavior<TRequest, TResponse>` | `IStreamPipelineBehavior<TRequest, TItem>` | |
| `IRequestPreProcessor<TRequest>` | An interceptor attribute (`IPreHandlerAttribute`), or a behavior | [Pre- and post-processors](#pre--and-post-processors) |
| `IRequestPostProcessor<TRequest, TResponse>` | An interceptor attribute (`IPostHandlerAttribute`), or a behavior | Runs on failure too, with the exception. |
| `IRequestExceptionHandler<TRequest, TResponse, TException>` | `IRequestExceptionHandler<TRequest, TResponse, TException>` (namespace `CQRSharp`) | `TResponse` is the response the request is dispatched with. [Exception handlers](#exception-handlers-and-actions) |
| `IRequestExceptionAction<TRequest, TException>` | `IRequestExceptionAction<TRequest, TException>` | Runs for every failure, handled or not. |
| `ForeachAwaitPublisher` (default), `TaskWhenAllPublisher` | `PublishStrategy.Sequential` (default), `PublishStrategy.Parallel` | Plus `ParallelWhenAllAggregate`. |
| A custom `INotificationPublisher` | none | Use a [notification behavior](notifications.md#notification-pipeline-behaviors) or the [outbox](outbox.md). |
| `cfg.Lifetime` | none | The dispatcher is scoped; handlers are transient unless you register them. |
| `cfg.RegisterGenericHandlers` | none | Closed handlers only (**CQRGEN009**). |
| Exceptions for expected failures | `CommandResult.NotFound(...)`, `Conflict(...)`, `Invalid(...)`, ... | [CommandResult](requests-and-handlers.md#commandresult) |

The type names MediatR and CQRSharp share (`IRequest`, `INotification`, `IPipelineBehavior<,>`, ...) matter while both
are referenced; [step 1](#step-1-add-cqrsharp-next-to-mediatr) explains how to keep them apart.

## The migration, step by step

The migration is incremental: both libraries run in one container, you move one feature (a request, its handler, its
validators and its callers) at a time, and remove MediatR when nothing uses it.

### Step 0: take stock

Before changing code, list what the move touches. Each item links to where this guide handles it:

- Request types declared as `record`s: they [become classes](#requests-are-classes).
- Open-generic request, notification or exception handlers: [not registered](#can-handlers-be-generic).
- Handlers that are `private` nested classes, or live in assemblies loaded at runtime: not discovered.
- Every behavior, pre-processor, post-processor, exception handler and exception action, and the order the behaviors
  are registered in: [Pipeline behaviors](#pipeline-behaviors).
- Code that catches exceptions thrown for expected failures (not found, conflict, validation).
- `IMediator`, `ISender` or `IPublisher` injected into singletons or resolved from the root provider:
  [Lifetimes](#lifetimes-scopes-and-cancellation).
- A custom `INotificationPublisher`, or `TaskWhenAllPublisher`: [Publish strategies](#publish-strategies).
- Notifications published as a base type, or handled through a base type or interface:
  [Which handlers run](#which-handlers-a-notification-reaches).

### Step 1: add CQRSharp next to MediatR

Add the package to every project that declares handlers and to the project that registers the services:

```bash
dotnet add package CQRSharp
```

Both libraries use the same names for their core contracts, in different namespaces:

| Name | MediatR namespace | CQRSharp namespace |
| --- | --- | --- |
| `IRequest`, `IRequest<T>`, `INotification`, `INotificationHandler<T>`, `IStreamRequest<T>`, `IStreamRequestHandler<,>` | `MediatR` | `CQRSharp` |
| `IPipelineBehavior<,>`, `RequestHandlerDelegate<T>`, `IStreamPipelineBehavior<,>`, `StreamHandlerDelegate<T>` | `MediatR` | `CQRSharp.Pipelines` |
| `IRequestExceptionHandler<,,>`, `IRequestExceptionAction<,>`, `RequestExceptionHandlerState<T>` | `MediatR.Pipeline` | `CQRSharp` |

The `CQRSharp` package adds `CQRSharp` and `CQRSharp.Pipelines` as [global usings](getting-started.md#global-usings), so
every file that also says `using MediatR;` and uses one of these names fails to compile with **CS0104** ("an ambiguous
reference"). For the duration of the migration, turn CQRSharp's global usings off in each project that references both:

```xml
<PropertyGroup>
  <!-- Until MediatR is removed: its INotification, IRequest and IPipelineBehavior clash with CQRSharp's. -->
  <CQRSharpImplicitUsings>false</CQRSharpImplicitUsings>
</PropertyGroup>
```

Your MediatR files then compile unchanged, and each migrated file imports what it uses (`using CQRSharp;`, and
`using CQRSharp.Pipelines;` in a behavior). In a file that needs both libraries, import one namespace and bring in the
few types you need from the other with aliases, or qualify them:

```csharp
using CQRSharp;
using SideBySide.Legacy;
using ISender = MediatR.ISender;
using IPublisher = MediatR.IPublisher;
```

A file-level alias takes precedence over a global using, so aliases also work with CQRSharp's global usings left on.
An alias cannot be generic, though (`using IRequest<T> = ...` is not C#), so a file that names `MediatR.IRequest<T>`
qualifies it instead.

Then register CQRSharp next to MediatR:

```csharp
// Both mediators in one container while the migration is under way.
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<GetCustomerName>());
builder.Services.AddCqrsGenerated(cqrs => cqrs.ValidateOnStart());
```

The two do not interfere: MediatR registers the types that implement its interfaces, the generator the types that
implement CQRSharp's. `ValidateOnStart()` makes the host check CQRSharp's wiring when it starts in every environment (in
Development it does so by default); see [Startup validation](configuration.md#startup-validation).

### Step 2: migrate a feature

Move one request at a time, with its handler, its validators, its exception hooks and its callers:

1. Change the request to derive from `CommandBase`, `ResultCommandBase<T>`, `QueryBase<T>` or `StreamRequestBase<T>`
   ([Requests and handlers](#requests-and-handlers)).
2. Change the handler interface and, for a command, its return type to `CommandResult`.
3. Change its callers from `ISender`/`IMediator` to `ICqrsDispatcher` and handle the returned `CommandResult`.
4. Move its validators, exception hooks and pre- or post-processors (the sections below).
5. Build. The analyzers point at what is left: **CQRA003** where a request is dispatched with no handler,
   **CQRGEN003** where a request has none, **CQRA004** for a stream sent with `Send`.

Each migrated handler can still call what has not moved yet, and MediatR handlers can call migrated requests: inject
the other library's façade. Here a migrated command calls a MediatR query and publishes a MediatR notification:

```csharp
public sealed class ShipOrder(int orderId) : CommandBase
{
    public int OrderId { get; } = orderId;
}

public sealed class ShipOrderHandler(ISender sender, IPublisher publisher) : ICommandHandler<ShipOrder>
{
    public async Task<CommandResult> Handle(ShipOrder command, CancellationToken cancellationToken)
    {
        var customer = await sender.Send(new GetCustomerName(command.OrderId), cancellationToken);
        Console.WriteLine($"  [CQRSharp] shipping order {command.OrderId} to {customer}");
        await publisher.Publish(new OrderShipped(command.OrderId), cancellationToken);
        return CommandResult.FromSuccess();
    }
}
```

and a MediatR handler that has not moved sends the migrated command (its file imports both namespaces, so the ambiguous
`IRequest<T>` is qualified):

```csharp
public sealed record ShipAllOrders(int[] OrderIds) : MediatR.IRequest<int>;

public sealed class ShipAllOrdersHandler(ICqrsDispatcher dispatcher) : IRequestHandler<ShipAllOrders, int>
{
    public async Task<int> Handle(ShipAllOrders request, CancellationToken cancellationToken)
    {
        var shipped = 0;
        foreach (var id in request.OrderIds)
        {
            CommandResult result = await dispatcher.Send(new ShipOrder(id), cancellationToken);
            if (result.IsSuccess) shipped++;
        }
        return shipped;
    }
}
```

`ICqrsDispatcher` is scoped, so a MediatR handler that injects it must itself be resolved from a scope: an `IMediator`
resolved from a scope (a controller, a minimal-API handler, a scope you create) is. Both dispatch in the same scope, so
they share its scoped services, such as a `DbContext`.

Migrate a notification together with all of its handlers: a MediatR handler does not receive a CQRSharp notification,
and the other way round. When a notification must reach handlers on both sides for a while, keep it a MediatR
notification and publish it through `IPublisher` from migrated code, as `ShipOrderHandler` does.

Behaviors apply to one library each: a MediatR behavior does not run for a CQRSharp request. While both run, a
cross-cutting concern such as logging or validation needs its CQRSharp counterpart registered as soon as the first
request moves ([Porting common behaviors](#porting-common-behaviors-to-the-built-ins)).

### Step 3: cut over

When no `MediatR` type is used any more:

1. Remove the `MediatR` package references and the `AddMediatR` call.
2. Remove `<CQRSharpImplicitUsings>false</CQRSharpImplicitUsings>`; the explicit `using CQRSharp;` and
   `using CQRSharp.Pipelines;` directives are now redundant and your IDE's "remove unnecessary usings" drops them.
3. Build with warnings as errors, and start the host once in Development (or with `ValidateOnStart()`), so the startup
   validator checks every binding.
4. Remove the MediatR-specific exception handling at your edges (middleware that mapped `ValidationException` and your
   own exceptions to HTTP) in favor of `CQRSharp.AspNetCore` ([ASP.NET Core](#aspnet-core)).

## Requests and handlers

### Requests are classes

CQRSharp requests derive from its base classes, which carry the [request context](requests-and-handlers.md#request-context).
A C# record cannot derive from a class, so a request declared as a positional record becomes a class. A primary
constructor keeps the call sites unchanged:

```csharp
// MediatR
public sealed record CreateOrder(string Customer, int Quantity) : IRequest<int>;
```

```csharp
// CQRSharp
public sealed class CreateOrder(string customer, int quantity) : ResultCommandBase<int>
{
    public string Customer { get; } = customer;
    public int Quantity { get; } = quantity;
}
```

Properties with `required` and `init` (`new CreateOrder { Customer = "Ada", Quantity = 2 }`) are the other common
style. A request object is [single-use](requests-and-handlers.md#requests-are-single-use): the dispatcher sets its
`Context`, so create a new one for each dispatch.

### Commands with a result

MediatR has one request kind; CQRSharp separates commands (change state, return a `CommandResult`) from queries (read,
return data). A MediatR request that creates something and returns its id maps to a value-returning command:

```csharp
// MediatR
public sealed class CreateOrderHandler(OrderStore store, IPublisher publisher) : IRequestHandler<CreateOrder, int>
{
    public async Task<int> Handle(CreateOrder request, CancellationToken cancellationToken)
    {
        var id = store.Add(request.Customer, request.Quantity);
        await publisher.Publish(new OrderPlaced(id, request.Customer, request.Quantity), cancellationToken);
        return id;
    }
}

// int id = await mediator.Send(new CreateOrder("Ada", 2));
```

```csharp
// CQRSharp
public sealed class CreateOrderHandler(OrderStore store, ICqrsDispatcher dispatcher)
    : IResultCommandHandler<CreateOrder, int>
{
    public async Task<CommandResult<int>> Handle(CreateOrder command, CancellationToken cancellationToken)
    {
        var id = store.Add(command.Customer, command.Quantity);
        await dispatcher.Publish(new OrderPlaced(id, command.Customer, command.Quantity), cancellationToken);
        return CommandResult<int>.FromSuccess(id);
    }
}

// CommandResult<int> result = await dispatcher.Send(new CreateOrder("Ada", 2));
// if (result.IsSuccess) Use(result.Value);
```

`CommandResult<T>` carries the outcome and, on success, `Value`. The CQRSharp way to write most such commands is a plain
`CommandBase` with an id the caller chooses (a `Guid`) and a query to read what it needs; `ResultCommandBase<T>` is
meant for a value no query can return ([Value-returning commands](requests-and-handlers.md#value-returning-commands)).
Both are valid migrations; the first changes less code.

### Commands without a result

A MediatR request without a response (`IRequest`, handled by `IRequestHandler<TRequest>` returning `Task`, surfacing as
`Unit` in behaviors) becomes a `CommandBase`, and its handler returns a `CommandResult`:

```csharp
// MediatR
public sealed record CancelOrder(int OrderId) : IRequest;

public sealed class CancelOrderHandler(OrderStore store) : IRequestHandler<CancelOrder>
{
    public Task Handle(CancelOrder request, CancellationToken cancellationToken)
    {
        if (!store.Cancel(request.OrderId))
            throw new OrderNotFoundException(request.OrderId);
        return Task.CompletedTask;
    }
}
```

```csharp
// CQRSharp
public sealed class CancelOrder(int orderId) : CommandBase
{
    public int OrderId { get; } = orderId;
}

public sealed class CancelOrderHandler(OrderStore store) : ICommandHandler<CancelOrder>
{
    public Task<CommandResult> Handle(CancelOrder command, CancellationToken cancellationToken)
        => Task.FromResult(store.Cancel(command.OrderId)
            ? CommandResult.FromSuccess()
            : CommandResult.NotFound($"Order {command.OrderId} was not found."));
}
```

Returning `CommandResult.NotFound(...)` instead of throwing is the idiomatic port: the caller checks `result.IsSuccess`
and `result.ErrorKind`, and `CQRSharp.AspNetCore` maps the kind to a status code. A handler may still throw; a thrown
exception propagates from `Send` as in MediatR, and an [exception handler](#exception-handlers-and-actions) can turn it
into a result. There is no `Unit`: `CommandResult` is the response of every command without a value.

### Queries

```csharp
// MediatR
public sealed record GetOrder(int OrderId) : IRequest<OrderDto?>;

public sealed class GetOrderHandler(OrderStore store) : IRequestHandler<GetOrder, OrderDto?>
{
    public Task<OrderDto?> Handle(GetOrder request, CancellationToken cancellationToken)
        => Task.FromResult(store.Find(request.OrderId));
}
```

```csharp
// CQRSharp
public sealed class GetOrder(int orderId) : QueryBase<OrderDto?>
{
    public int OrderId { get; } = orderId;
}

public sealed class GetOrderHandler(OrderStore store) : IQueryHandler<GetOrder, OrderDto?>
{
    public Task<OrderDto?> Handle(GetOrder query, CancellationToken cancellationToken)
        => Task.FromResult(store.Find(query.OrderId));
}

// OrderDto? order = await dispatcher.Send(new GetOrder(1));
```

A query returns its result directly, as in MediatR.

### Streams

```csharp
// MediatR
public sealed record ListOrders : IStreamRequest<OrderDto>;

// await foreach (var order in mediator.CreateStream(new ListOrders())) { ... }
```

```csharp
// CQRSharp
public sealed class ListOrders : StreamRequestBase<OrderDto>;

public sealed class ListOrdersHandler(OrderStore store) : IStreamRequestHandler<ListOrders, OrderDto>
{
    public async IAsyncEnumerable<OrderDto> Handle(ListOrders request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var order in store.All())
        {
            await Task.Yield();   // stands in for reading the next row
            cancellationToken.ThrowIfCancellationRequested();
            yield return order;
        }
    }
}

// await foreach (var order in dispatcher.Stream(new ListOrders())) { ... }
```

The handler body does not change. `CreateStream` becomes `Stream`. Sending a stream request with `Send` compiles, since a
stream request is also an `IRequest<IAsyncEnumerable<T>>`, but fails at runtime; **CQRA004** makes it a build error and
its code fix rewrites the call.

### Registration

```csharp
// MediatR
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<CreateOrder>();
    cfg.AddOpenBehavior(typeof(LoggingBehavior<,>));
    cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
    cfg.AddRequestPreProcessor<CreateOrderPreProcessor>();
});
```

```csharp
// CQRSharp
builder.Services.AddCqrsGenerated(cqrs => cqrs
    .UseFluentValidation()
    .ValidateOnStart());
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(RequestLoggingBehavior<,>));
```

`AddCqrsGenerated` is generated into your assembly. It registers the dispatcher and every handler, validator, exception
hook, context factory and closed behavior in the assembly and in the CQRSharp assemblies it references, plus the
validation and exception-handling behaviors. There are no assemblies to list: in a solution with several projects, one
call in the host wires them all ([Multi-assembly applications](source-generator.md#multi-assembly-applications)). Call
`AddCqrsGenerated`, not `AddCqrs`; **CQRA014** reports the latter.

## Notifications

Notifications keep their shape: implement `INotification` (now `CQRSharp.INotification`), handle with
`INotificationHandler<T>`, publish with `Publish`. Records work as before.

```csharp
public sealed record OrderPlaced(int OrderId, string Customer, int Quantity) : INotification;

public sealed class EmailCustomer(ILogger<EmailCustomer> logger) : INotificationHandler<OrderPlaced>
{
    public Task Handle(OrderPlaced notification, CancellationToken cancellationToken)
    {
        logger.LogInformation("[email] order {OrderId} confirmed to {Customer}", notification.OrderId, notification.Customer);
        return Task.CompletedTask;
    }
}
```

This code is the same in both libraries except for the namespace. What differs is which handlers run, in what order,
and how failures surface.

### Which handlers a notification reaches

MediatR resolves `IEnumerable<INotificationHandler<R>>` from the container for the notification's runtime type `R`.
A handler for a base type or an interface of `R` runs only when its assembly scan registered it for `R`, which it does when
some *other* handler in the scanned assemblies handles `R` itself. CQRSharp's rule is independent of the other handlers:
every handler declared for `R`, for a base class of `R` or for an interface `R` implements runs, each once
([Which handlers a notification reaches](notifications.md#which-handlers-a-notification-reaches)).

With `record BaseEvent : INotification`, `DerivedEvent : BaseEvent` and `UnhandledDerivedEvent : BaseEvent`, a
`BaseHandler` for `BaseEvent` and a `DerivedHandler` for `DerivedEvent`, the two libraries run:

| Published | MediatR 12.5 runs | CQRSharp 5.0 runs |
| --- | --- | --- |
| `DerivedEvent`, as itself or as `INotification` | `BaseHandler`, `DerivedHandler` | `BaseHandler`, `DerivedHandler` |
| `UnhandledDerivedEvent` | nothing | `BaseHandler` |
| `BaseEvent` | `BaseHandler` | `BaseHandler` |

Both dispatch by runtime type, so publishing a notification typed as `INotification` (a list of domain events) reaches
the same handlers as publishing it as its own type. Where MediatR skipped a base-type handler, a migrated application
starts running it: check handlers declared for base types and interfaces.

An open-generic handler (`AuditAll<T> : INotificationHandler<T>`), which MediatR registers for every notification, is
not registered by CQRSharp (**CQRGEN009**). To observe every notification, write an open-generic
[notification behavior](notifications.md#notification-pipeline-behaviors) instead.

### Publish strategies

| MediatR | CQRSharp | Behavior |
| --- | --- | --- |
| `ForeachAwaitPublisher` (default) | `PublishStrategy.Sequential` (default) | One handler at a time; the first failure stops the rest and propagates. |
| `TaskWhenAllPublisher` | `PublishStrategy.Parallel` | All start at once; every handler runs; one failure propagates. |
| none | `PublishStrategy.ParallelWhenAllAggregate` | As `Parallel`, but more than one failure is thrown as an `AggregateException`. |
| a custom `INotificationPublisher` | none | |

```csharp
// MediatR
services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<Program>();
    cfg.NotificationPublisherType = typeof(TaskWhenAllPublisher);
});

// CQRSharp
services.AddCqrsGenerated(b => b.ConfigureNotifications(o => o.PublishStrategy = PublishStrategy.Parallel));
```

Under `Sequential`, MediatR runs handlers in container registration order, which follows its assembly scan. CQRSharp
orders generated handlers by their stable handler name (the namespace-qualified type name), then handlers you registered
by hand in registration order. Do not depend on either order; if one handler must run after another, publish a second
notification or do both in one handler.

Handlers run in the publishing DI scope in both libraries. What MediatR leaves to you, CQRSharp also offers: notification
behaviors, [lifecycle notifications](notifications.md#lifecycle-notifications) around every request, and durable delivery
through the [outbox](outbox.md).

## Pipeline behaviors

The contract is nearly the same. MediatR's:

```csharp
public sealed class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var name = typeof(TRequest).Name;
        logger.LogInformation("[log] -> {Request}", name);
        try
        {
            var response = await next();
            logger.LogInformation("[log] <- {Request}", name);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogInformation("[log] <- {Request} failed: {Error}", name, ex.GetType().Name);
            throw;
        }
    }
}
```

and ported:

```csharp
public sealed class RequestLoggingBehavior<TRequest, TResult>(ILogger<RequestLoggingBehavior<TRequest, TResult>> logger)
    : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    // Outside validation, as the behavior was in the MediatR pipeline. Without a priority it would run inside every
    // built-in behavior, and never see a request that validation rejected.
    public int PipelineExecutionPriority => CqrsPipelinePriorities.Validation - 1;

    public async Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next,
        CancellationToken cancellationToken)
    {
        var name = typeof(TRequest).Name;
        logger.LogInformation("[log] -> {Request}", name);
        try
        {
            var result = await next();
            logger.LogInformation("[log] <- {Request}", name);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogInformation("[log] <- {Request} failed: {Error}", name, ex.GetType().Name);
            throw;
        }
    }
}
```

What changes:

- **The namespace** is `CQRSharp.Pipelines`, and the constraint is `where TRequest : IRequest` (CQRSharp's) instead of
  `notnull`.
- **The name.** `CQRSharp.Pipelines` already contains `LoggingBehavior<,>`, `ValidationBehavior<,>`,
  `UnitOfWorkBehavior<,>`, `TimeoutBehavior<,>`, `ResilienceBehavior<,>`, `IdempotencyBehavior<,>`,
  `RateLimitingBehavior<,>` and `ExceptionHandlingBehavior<,>` (and their `Stream...` forms). A behavior of yours with one
  of these names is ambiguous (**CS0104**) in every file that also imports `CQRSharp.Pipelines`, which with the global
  usings is every file. Rename it, as above, or better, replace it with the built-in
  ([Porting common behaviors](#porting-common-behaviors-to-the-built-ins)).
- **`next`** takes an optional token in both: `next()` passes on the token the behavior received, `next(token)` another.
- **The response of a command** is `CommandResult` or `CommandResult<T>`, never `Unit`. A behavior that tested
  `typeof(TResponse) == typeof(Unit)` tests for `CommandResult`, and a behavior can read a command's outcome
  (`result is CommandResult { IsSuccess: false }`) without catching anything.
- **Order is by priority, not registration.** MediatR nests behaviors in the order they are registered, the first one
  outermost, with the exception-handler and pre- and post-processor behaviors that `AddMediatR` registers ahead of those
  you add with `cfg.AddOpenBehavior`. CQRSharp orders every behavior by `IPrioritizedPipelineBehavior.PipelineExecutionPriority`,
  lower outermost; the built-ins sit at fixed priorities, and a behavior with no priority runs inside all of them, next
  to the handler ([Execution order](pipeline-behaviors.md#execution-order)). To keep a ported behavior where it was,
  give it a priority relative to `CqrsPipelinePriorities`, as above.
- **Registration.** An open-generic behavior is registered on the service collection, before or after
  `AddCqrsGenerated`, with the lifetime you choose. A closed behavior (`IPipelineBehavior<CreateOrder, CommandResult<int>>`)
  that is `public` or `internal` is registered by the generator. `cfg.AddBehavior`, `AddOpenBehavior` and
  `AddStreamBehavior` have no builder equivalent.
- **Streams** have their own contract in both: `IStreamPipelineBehavior<TRequest, TItem>`, whose `next` also takes an
  optional token in CQRSharp.
- **Native AOT.** Under Native AOT the container cannot close an open-generic behavior over a value-type result
  (`QueryBase<int>`). The generator emits closed factories for the behaviors it can see, and a behavior it cannot close
  fails the dispatch rather than being skipped ([Native AOT](native-aot.md#value-type-results-and-notifications)). On the
  JIT nothing changes.

A request can opt out of a behavior with `[PipelineExemption(typeof(LoggingBehavior<,>))]`
([Pipeline exemptions](pipeline-behaviors.md#pipeline-exemptions)); MediatR has no equivalent short of a marker the
behavior checks.

## Pre- and post-processors

MediatR runs `IRequestPreProcessor<T>` and `IRequestPostProcessor<T, R>` through two behaviors that `AddMediatR`
registers ahead of the behaviors you add with `cfg.AddOpenBehavior`, so a pre-processor runs before your logging and
validation behaviors, and a post-processor runs only when the handler succeeded.

CQRSharp's nearest equivalent is an **interceptor**: an attribute on the request that implements
`IPreHandlerAttribute`, `IPostHandlerAttribute` or both ([Interceptors](pipeline-behaviors.md#interceptors-pre--and-post-handler-attributes)).
It runs at the center of the pipeline, around the handler, inside every behavior:

```csharp
// MediatR
public sealed class CreateOrderPreProcessor(ILogger<CreateOrderPreProcessor> logger) : IRequestPreProcessor<CreateOrder>
{
    public Task Process(CreateOrder request, CancellationToken cancellationToken)
    {
        logger.LogInformation("[pre] new order for '{Customer}'", request.Customer);
        return Task.CompletedTask;
    }
}

// cfg.AddRequestPreProcessor<CreateOrderPreProcessor>();
```

```csharp
// CQRSharp
[LogNewOrder]
public sealed class CreateOrder(string customer, int quantity) : ResultCommandBase<int>
{
    public string Customer { get; } = customer;
    public int Quantity { get; } = quantity;
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class LogNewOrderAttribute : Attribute, IPreHandlerAttribute
{
    public int PreHandlerExecutionPriority => 0;

    public Task OnBeforeHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var logger = serviceProvider.GetRequiredService<ILogger<LogNewOrderAttribute>>();
        logger.LogInformation("[pre] new order for '{Customer}'", ((CreateOrder)request).Customer);
        return Task.CompletedTask;
    }
}
```

With the logging behavior of [Pipeline behaviors](#pipeline-behaviors) registered in both, a valid and an invalid
`CreateOrder` log this with MediatR:

```
[pre] new order for 'Ada'
[log] -> CreateOrder
[email] order 1 confirmed to Ada
[stock] 2 item(s) reserved for order 1
[log] <- CreateOrder
[pre] new order for ''
[log] -> CreateOrder
[log] <- CreateOrder failed: ValidationException
```

and this with CQRSharp:

```
[log] -> CreateOrder
[pre] new order for 'Ada'
[email] order 1 confirmed to Ada
[stock] 2 item(s) reserved for order 1
[log] <- CreateOrder
[log] -> CreateOrder
[log] <- CreateOrder failed: RequestValidationException
```

The differences to plan for:

- **Position.** An interceptor runs after validation, so it never sees a request validation rejected; a MediatR
  pre-processor did. If a pre-processor normalizes input that validation must see, port it to a behavior with a
  priority below `CqrsPipelinePriorities.Validation` instead.
- **Dependencies.** An attribute has no constructor injection; it resolves services from the `IServiceProvider` it is
  given, which is the dispatching scope's.
- **Post-handlers see failures too.** `OnAfterHandle` receives a `RequestOutcome`: the result, or the exception the request
  failed with (`outcome.Threw`). A ported post-processor that should run only on success checks `outcome.Threw` first.
- **Scope of one attribute.** An interceptor applies to the request it decorates and to requests derived from it. An
  open-generic pre-processor (`IRequestPreProcessor<TRequest>` for every request) becomes a behavior.

A closed behavior is the other port for a per-request pre- or post-processor: `IPipelineBehavior<CreateOrder,
CommandResult<int>>` gets constructor injection, is registered by the generator, and runs wherever its priority puts it.

## Exception handlers and actions

CQRSharp has the same two contracts, in the `CQRSharp` namespace: `IRequestExceptionHandler<TRequest, TResponse,
TException>`, which can supply a response with `state.SetHandled(...)`, and `IRequestExceptionAction<TRequest,
TException>`, which only observes. The generator registers them; nothing is scanned. The port below keeps a
`CancelOrderHandler` that throws `OrderNotFoundException`, as the MediatR one did.

```csharp
// MediatR
public sealed class CancelOrderNotFoundHandler(ILogger<CancelOrderNotFoundHandler> logger)
    : IRequestExceptionHandler<CancelOrder, Unit, OrderNotFoundException>
{
    public Task Handle(CancelOrder request, OrderNotFoundException exception,
        RequestExceptionHandlerState<Unit> state, CancellationToken cancellationToken)
    {
        logger.LogInformation("[exception] {Message} Nothing to cancel.", exception.Message);
        state.SetHandled(Unit.Value);
        return Task.CompletedTask;
    }
}
```

```csharp
// CQRSharp
public sealed class CancelOrderNotFoundHandler(ILogger<CancelOrderNotFoundHandler> logger)
    : IRequestExceptionHandler<CancelOrder, CommandResult, OrderNotFoundException>
{
    public Task Handle(CancelOrder request, OrderNotFoundException exception,
        RequestExceptionHandlerState<CommandResult> state, CancellationToken cancellationToken)
    {
        logger.LogInformation("[exception] {Message} Nothing to cancel.", exception.Message);
        state.SetHandled(CommandResult.NotFound(exception.Message));
        return Task.CompletedTask;
    }
}
```

- **`TResponse` is the response the request is dispatched with**: `CommandResult` where MediatR had `Unit`,
  `CommandResult<T>` for a value-returning command, the query's result type, `IAsyncEnumerable<T>` for a stream. A hook
  declared over another type never runs, and the generator reports it (**CQRGEN017**).
- **A handled exception can report the failure.** Where the MediatR hook answered `Unit` and the caller saw success, the
  port can answer a failed `CommandResult`, so the caller learns the order was not there. Answering
  `CommandResult.FromSuccess()` keeps MediatR's behavior exactly. Often the better port removes the exception: the
  command handler returns `CommandResult.NotFound(...)` itself.
- **Hooks are matched to the exact request type.** A hook runs for the request type it names. MediatR can also run a
  hook declared for a base request type, and an open-generic hook for every request. CQRSharp registers no open-generic
  hook, and a hook declared for an abstract base request compiles without a diagnostic but never runs for the derived
  requests. A cross-cutting exception policy is a behavior (at a priority just inside
  `CqrsPipelinePriorities.ExceptionHandling`).
- **Actions run for every failure.** CQRSharp runs every matching action first, then the handlers. MediatR's default
  (`RequestExceptionActionProcessorStrategy.ApplyForUnhandledExceptions`) runs actions only for exceptions no handler
  handled; CQRSharp's behavior is that of `ApplyForAllExceptions`.
- **The caller's own cancellation runs no hook**: an `OperationCanceledException` after the caller's token was cancelled
  passes straight through.
- The exception-handling behavior is the outermost one in both libraries, so hooks also see exceptions thrown by
  validation and other behaviors.

## Porting common behaviors to the built-ins

Most MediatR applications carry a handful of hand-written behaviors. CQRSharp ships these; delete yours and call the
verb. Each is described in [Pipeline behaviors](pipeline-behaviors.md) and [Configuration](configuration.md#the-fluent-builder).

### Logging

`UseLogging()` logs every request and stream with its elapsed time, at a level that follows who has to act
([Observability](observability.md#logging)). It runs inside exception handling and rate limiting and outside
validation, so it also logs rejected requests. Keep a behavior of your own only if you need a different format.

### Validation with FluentValidation

The usual MediatR validation behavior:

```csharp
public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(request, cancellationToken);
            failures.AddRange(result.Errors);
        }

        if (failures.Count > 0)
            throw new ValidationException(failures);

        return await next();
    }
}
```

is replaced by the `CQRSharp.FluentValidation` package, and your `AbstractValidator<T>` classes stay as they are:

```bash
dotnet add package CQRSharp.FluentValidation
```

```csharp
builder.Services.AddValidatorsFromAssemblyContaining<CreateOrderValidator>();
builder.Services.AddCqrsGenerated(cqrs => cqrs.UseFluentValidation());
```

Failures throw `RequestValidationException` (namespace `CQRSharp`) instead of FluentValidation's `ValidationException`;
its `Failures` carry each failure's code, message and member name. Only `Severity.Error` failures reject a request
([FluentValidation](fluentvalidation.md#severity)). Update the code that catches the exception:

```csharp
try
{
    await dispatcher.Send(new CreateOrder("", 0));
}
catch (RequestValidationException ex)
{
    Console.WriteLine($"rejected: {string.Join(" ", ex.Failures.Select(f => f.Message))}");
}
```

FluentValidation is not Native-AOT compatible; under AOT, write `IRequestValidator<T>` classes, which the generator
registers ([Validation](pipeline-behaviors.md#validation)).

### Transactions and unit of work

A MediatR transaction behavior over EF Core:

```csharp
public sealed class TransactionBehavior<TRequest, TResponse>(AppDbContext db) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not ITransactionalRequest || db.Database.CurrentTransaction is not null)
            return await next();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var response = await next();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }
}
```

becomes a verb from `CQRSharp.EntityFrameworkCore` and a marker on each transactional command:

```csharp
services.AddCqrsGenerated(b => b.UseEntityFrameworkCoreUnitOfWork<AppDbContext>());

public sealed class PlaceOrder : CommandBase, ITransactionalCommand
{
    public IsolationLevel IsolationLevel => IsolationLevel.Unspecified;   // the configured default
    public required string Customer { get; init; }
}
```

The unit of work saves and commits when the handler succeeds, and rolls back when it throws **or returns a failed
`CommandResult`**, which a hand-written behavior usually does not do. A request sent from a handler in the same scope
takes part in the open transaction. Queries opt in with `ITransactionalQuery`. See [Unit of work](unit-of-work.md).

### Retries

A retry behavior (hand-written or over Polly) becomes `UseResilience` and the `IRetryableRequest` marker:

```csharp
services.AddCqrsGenerated(b => b
    .UseResilience(o =>
    {
        o.MaxRetries = 3;
        o.BaseDelay = TimeSpan.FromMilliseconds(200);
        o.BackoffMultiplier = 2.0;
    }));

public sealed class GetExchangeRate : QueryBase<decimal>, IRetryableRequest
{
    public required string Currency { get; init; }
}
```

Only requests with the marker are retried, and only on exceptions: a returned failed `CommandResult`, a validation
failure, the timeout behavior's `RequestTimeoutException` and the caller's cancellation are not retried. The retry runs outside the unit
of work, so each attempt gets a fresh transaction ([Resilience](idempotency-and-resilience.md#resilience--retries)).

### Everything together

```csharp
services.AddCqrsGenerated(b => b
    .UseLogging()
    .UseFluentValidation()
    .UseEntityFrameworkCoreUnitOfWork<AppDbContext>()
    .UseResilience(o =>
    {
        o.MaxRetries = 3;
        o.BaseDelay = TimeSpan.FromMilliseconds(200);
        o.BackoffMultiplier = 2.0;
    }));
```

The order of the verbs does not matter: each behavior has a fixed place. Timeouts (`UseTimeout`), rate limiting
(`UseRateLimiting`) and idempotency (`UseIdempotency`) replace their hand-written counterparts the same way.

## Lifetimes, scopes and cancellation

- **The dispatcher is scoped.** Resolve `ICqrsDispatcher` from a scope: controllers and minimal-API handlers already run
  in one. A singleton that injected `IMediator` (a hosted or background service, a message consumer) takes an
  `IServiceScopeFactory` and creates a scope per unit of work:

  ```csharp
  using var scope = scopeFactory.CreateScope();
  var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
  ```

  Resolving it from the root provider makes its scoped dependencies live for the process; `ValidateScopes` (on in
  Development) reports that.
- **Handlers are transient**, as in MediatR. To choose another lifetime, register the handler's concrete type yourself
  before `AddCqrsGenerated` (`services.AddScoped<CreateOrderHandler>()`); the generated registration then leaves it
  alone ([Hosting and lifetimes](configuration.md#hosting-and-lifetimes)).
- **Nested dispatch shares the scope** by default (`ExecutionScopeMode.Current`), as in MediatR: a request sent from a
  handler sees the same `DbContext`. `ExecutionScopeMode.New` gives each request a scope of its own
  ([ScopeMode](configuration.md#scopemode)).
- **Cancellation** works as in MediatR: the token passed to `Send`, `Stream` or `Publish` reaches the handler and every
  behavior. Behaviors that add a deadline (`UseTimeout`) link their own.

## Tests

Handlers are plain classes in both libraries, so direct handler tests only change the request and result types. Code
that *calls* the mediator was typically tested with a mock of `ISender`:

```csharp
// MediatR, with Moq
var id = Guid.NewGuid();
var sender = new Mock<ISender>();
sender.Setup(s => s.Send(It.Is<GetOrder>(q => q.OrderId == id), It.IsAny<CancellationToken>()))
      .ReturnsAsync(new OrderDto(id, "Ada"));

var cancelled = await new OrderService(sender.Object).TryCancelAsync(id);

Assert.True(cancelled);
sender.Verify(s => s.Send(It.Is<CancelOrder>(c => c.OrderId == id), It.IsAny<CancellationToken>()), Times.Once);
```

`ICqrsDispatcher` can be mocked the same way, but `CQRSharp.Testing` ships a fake that stubs by request type and records
what was sent, published and streamed:

```bash
dotnet add package CQRSharp.Testing
```

```csharp
// CQRSharp
using CQRSharp.Testing;

var id = Guid.NewGuid();
var dispatcher = new RecordingCqrsDispatcher()
    .Setup<GetOrder, OrderDto?>(query => new OrderDto(query.OrderId, "Ada"));

var cancelled = await new OrderService(dispatcher).TryCancelAsync(id);

Assert.True(cancelled);   // an unstubbed CancelOrder (a plain ICommand) succeeds
Assert.Equal(id, Assert.Single(dispatcher.Sent<CancelOrder>()).OrderId);
```

An unstubbed plain command answers `CommandResult.FromSuccess()`; stub a failure with
`.Setup<CancelOrder, CommandResult>(CommandResult.Conflict("Order already shipped."))`. A request that returns a value
must be stubbed, or `Send` throws naming it. [The testing packages](testing-package.md#recordingcqrsdispatcher-reference)
has the full API.

Tests that go through the real pipeline build a host with `AddCqrsGenerated` and resolve the dispatcher from a scope
([Testing through the dispatcher](testing.md#testing-through-the-dispatcher)). A test project that references the
application under test and sees its internals may need the qualified entry point (**CQRGEN015**).

## ASP.NET Core

Endpoints inject `ICqrsDispatcher` where they injected `ISender`. With MediatR, an expected failure was an exception the
endpoint or a middleware translated; with CQRSharp it is a `CommandResult`, and `CQRSharp.AspNetCore` turns it into an
`IResult`:

```bash
dotnet add package CQRSharp.AspNetCore
```

```csharp
// MediatR
app.MapDelete("/orders/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
{
    try
    {
        await sender.Send(new CancelOrder(id), ct);
        return Results.NoContent();
    }
    catch (OrderNotFoundException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
    }
});
```

```csharp
// CQRSharp
using CQRSharp.AspNetCore;

app.MapDelete("/orders/{id:guid}", async (Guid id, ICqrsDispatcher dispatcher, CancellationToken ct) =>
{
    CommandResult result = await dispatcher.Send(new CancelOrder { OrderId = id }, ct);
    return result.ToHttpResult();   // 204, or a ProblemDetails whose status follows result.ErrorKind (404 here)
});
```

A controller action can return the same `IResult`:

```csharp
[HttpDelete("{id:guid}")]
public async Task<IResult> Cancel(Guid id, CancellationToken ct)
    => (await dispatcher.Send(new CancelOrder { OrderId = id }, ct)).ToHttpResult();
```

The exceptions the pipeline throws (`RequestValidationException`, and those of the rate-limiting, timeout and idempotency
behaviors) are mapped to ProblemDetails by one registration, replacing the middleware that translated FluentValidation's
`ValidationException`:

```csharp
builder.Services.AddCqrsProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();
```

Queries return their data directly, so a `GET` endpoint changes only the injected type. The status for each error kind
and each exception is listed in [ASP.NET Core](aspnetcore.md).

## Pitfalls checklist

What goes wrong most often in a migration, and what reports it:

- [ ] **A handler the generator cannot see.** A `private` or `protected` nested handler is skipped (**CQRGEN006**), and
      so is a validator or exception hook the generator cannot see; MediatR's scan found them. Make them `internal` or
      `public`.
- [ ] **An open-generic handler** is not registered (**CQRGEN009**). Close it, or derive a closed class from it (see the
      [FAQ](#can-handlers-be-generic)).
- [ ] **A request without a handler**: **CQRGEN003** where it is declared, **CQRA003** where it is dispatched; the first
      dispatch throws.
- [ ] **Two handlers for one request**: **CQRGEN004**, an error. MediatR silently kept the first it registered.
- [ ] **A stream sent with `Send`**: **CQRA004**, an error with a code fix to `Stream`.
- [ ] **`AddCqrs()` instead of `AddCqrsGenerated()`**: **CQRA014**, an error with a code fix.
- [ ] **An exception hook over the wrong response type** (`Unit` left over, or `bool` for a command): **CQRGEN017**.
- [ ] **A handler project without the generator** (it references only `CQRSharp.Abstractions`): **CQRA010**. Reference
      `CQRSharp` from every project that declares handlers.
- [ ] **`CS0104` ambiguous references** while both libraries are referenced: set `CQRSharpImplicitUsings` to `false`
      ([step 1](#step-1-add-cqrsharp-next-to-mediatr)).
- [ ] **A behavior named like a built-in** (`LoggingBehavior<,>`, `ValidationBehavior<,>`, ...) is ambiguous with
      `CQRSharp.Pipelines`' own (**CS0104**): rename it or replace it with the built-in.
- [ ] **The dispatcher resolved from the root provider or injected into a singleton**: an error under `ValidateScopes`
      (on in Development). Create a scope.
- [ ] **A behavior that moved position.** A ported behavior with no priority runs inside every built-in; give it one
      if it must run outside validation or the unit of work.
- [ ] **A pre-processor that ran before validation** now runs after it as an interceptor.
- [ ] **Catch blocks for failures that are now results.** A `try`/`catch` around `Send` for "not found" never fires once
      the handler returns `CommandResult.NotFound`; check `result.IsSuccess`. A caller that ignores the returned
      `CommandResult` ignores the failure.
- [ ] **`ValidationException` catch blocks** become `RequestValidationException`.
- [ ] **Base-type notification handlers that start running**: CQRSharp delivers to handlers of base types and interfaces
      whether or not another handler handles the derived type.
- [ ] **A reused request instance.** Requests are single-use; create a new one per dispatch.
- [ ] **Validators for a base request** are not applied to derived requests by FluentValidation's container lookup;
      register them for each request type.
- [ ] **An idempotent or retryable request without its verb**: **CQRA018** (missing `UseIdempotency`) and **CQRA019**
      (missing `UseResilience`) at build, **CQRCONF005** and **CQRCONF006** at runtime.

Two checks run without being asked: the [startup validator](diagnostics.md#startup-validation-cqrconf) in the
Development environment (everywhere with `ValidateOnStart()`), and the [first-use checks](diagnostics.md#first-use-checks)
at the first dispatch of each request in every environment. `ICqrsDiagnostics.DescribeAllRequests()` lists how every
request is bound, which makes a quick test that every request you expect has a handler and the behaviors you expect
([The introspection API](diagnostics.md#the-introspection-api)).

## FAQ

### Where did `Unit` go?

A command without a value returns `CommandResult`, which says whether it succeeded and why not. Behaviors and callers
that dealt with `Unit` deal with `CommandResult` instead; there is no `Unit` type.

### Can I send a request by its runtime type, as an `object`?

Yes. `Send(object)` and `Stream(object)` dispatch by the request's runtime type and return boxed results, like MediatR's
`Send(object)` and `CreateStream(object)`. There is no `Publish(object)`: cast to `INotification`
(`dispatcher.Publish((INotification)message)`), which reaches the handlers of the runtime type.

### Can handlers be generic?

Not open-generic. A handler must be a closed, non-generic class (**CQRGEN009** reports `Handler<T>`); MediatR's
`RegisterGenericHandlers` option has no equivalent. A closed class may derive from a generic base, so shared logic stays in one
place:

```csharp
public sealed class Echo<T>(T value) : QueryBase<T>
{
    public T Value { get; } = value;
}

public abstract class EchoHandlerBase<T> : IQueryHandler<Echo<T>, T>
{
    public Task<T> Handle(Echo<T> query, CancellationToken ct) => Task.FromResult(query.Value);
}

public sealed class EchoIntHandler : EchoHandlerBase<int>;   // serves Echo<int>
```

Behaviors can be open-generic, as in MediatR. Notification handlers and exception hooks must be closed.

### How do I publish in parallel?

`ConfigureNotifications(o => o.PublishStrategy = PublishStrategy.Parallel)`, the counterpart of `TaskWhenAllPublisher`.
The handlers share the publishing DI scope, so only do this when they do not share a service that is not thread-safe,
such as a `DbContext` ([Publish strategies](notifications.md#publish-strategies)).

### Can a request still be a record?

A request that derives from a CQRSharp base class cannot be a record. Implementing `IQuery<T>` or `ICommand` directly on a
record, with a settable `Context` property, compiles and dispatches, but the context the dispatcher sets then takes part
in the record's equality and `ToString`. Prefer a class.

### Can I keep throwing exceptions from command handlers?

Yes: a thrown exception propagates from `Send` as in MediatR, and runs the exception hooks, the lifecycle *Failed*
notification and the unit of work's rollback. `CommandResult` exists for expected outcomes a caller should branch on;
use exceptions for the unexpected.

### Can I inject the dispatcher into a singleton?

Not directly: it is scoped. Inject `IServiceScopeFactory` and create a scope per operation (see
[Lifetimes](#lifetimes-scopes-and-cancellation)).

### Does CQRSharp find handlers in other projects?

Every project that declares handlers references `CQRSharp`, so the generator writes a module for it, and the project that
calls `AddCqrsGenerated()` composes the modules of every project it references. No assemblies are named
([Multi-assembly applications](source-generator.md#multi-assembly-applications)). An assembly loaded at runtime that
the host does not reference is not composed.

### Can I plug in my own `INotificationPublisher`?

No. Choose one of the three strategies; to wrap every publish (tracing, suppression), use a notification behavior, and for
delivery that survives a crash, the outbox.

### Is there an equivalent of MediatR's `IMediator` for code that only publishes?

`ICqrsDispatcher` is the one façade; there is no separate sender or publisher interface. Code that should only publish
takes `ICqrsDispatcher` and calls `Publish`.
