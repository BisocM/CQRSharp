using Microsoft.CodeAnalysis;

namespace CQRSharp.Analyzers;

/// <summary>
///     Diagnostic descriptors raised by the CQRSharp analyzers to help consumers use the framework correctly.
/// </summary>
internal static class CqrsDiagnostics
{
    public const string Category = "CQRSharp.Usage";

    // Errors: the analyzer is certain the code does not do what it says.

    public static readonly DiagnosticDescriptor SendStreamRequest = new(
        "CQRA004",
        "Stream request sent via Send",
        "'{0}' is a stream request and must be dispatched with Stream(...), not Send(...)",
        Category,
        DiagnosticSeverity.Error,
        true,
        "An IStreamRequest<T> is also an IRequest<IAsyncEnumerable<T>>, so the C# compiler accepts it in ICqrsDispatcher.Send, but the dispatcher rejects it at runtime; dispatch it with Stream(...) instead.");

    public static readonly DiagnosticDescriptor PipelineExemptionHasNoEffect = new(
        "CQRA005",
        "PipelineExemption has no effect",
        "[PipelineExemption(typeof({0}))] has no effect: {1}",
        Category,
        DiagnosticSeverity.Error,
        true,
        "An exemption is read from the request being dispatched and matched against the concrete behavior type that runs for it (or that type's open-generic definition). It does nothing when it names a type that is not a pipeline behavior, an abstract behavior or an interface, a behavior that never runs for the request (closed over another request or result, or of the other pipeline kind), or when it sits on a type that is not a request.");

    public static readonly DiagnosticDescriptor DirectCoreRegistration = new(
        "CQRA014",
        "AddCqrs() leaves dispatch unrouted — call AddCqrsGenerated()",
        "'AddCqrs()' registers the dispatcher but not the source-generated handler routing, so the first Send/Stream/Publish throws at runtime. Call AddCqrsGenerated() instead, or AddCqrsGenerated(b => ...) to configure CQRSharp.",
        Category,
        DiagnosticSeverity.Error,
        true,
        "AddCqrs is the low-level core registration used by the fluent builder and the generated bootstrap; calling it directly from your code wires the dispatcher without the generated routing. AddCqrsGenerated() registers both.");

    public static readonly DiagnosticDescriptor IdempotentRequestWithoutIdempotency = new(
        "CQRA018",
        "IIdempotentRequest without UseIdempotency",
        "{0} implement IIdempotentRequest, but this CQRSharp configuration never calls UseIdempotency(...), so no idempotency behavior is registered and duplicates would be processed; dispatching them fails at runtime with CQRCONF005. Call UseIdempotency(...) on the builder.",
        Category,
        DiagnosticSeverity.Error,
        true,
        "IIdempotentRequest is a request's contract that a repeated delivery is not processed twice, which only the idempotency behavior that UseIdempotency(...) registers keeps. Reported only when the application's whole CQRSharp configuration is in view: one AddCqrsGenerated call made of builder verbs, in an application no referenced assembly can configure further.",
        customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

    // Warnings and suggestions: handlers may be registered another way (manual DI, a project this one cannot see), and a
    // notification with no subscriber is legal, so these cannot be certain the code is broken.

    public static readonly DiagnosticDescriptor NoHandlerForRequest = new(
        "CQRA003",
        "No handler found for the dispatched request",
        "No handler was found for request '{0}' in this compilation or referenced assemblies; dispatching it will fail at runtime unless a handler is registered another way",
        Category,
        DiagnosticSeverity.Warning,
        true,
        "Send/Stream of a request with no discoverable handler throws at runtime.",
        customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

    public static readonly DiagnosticDescriptor NoSubscriberForNotification = new(
        "CQRA006",
        "No subscriber found for the published notification",
        "No handler was found for notification '{0}' in this compilation or referenced assemblies; the publish will be a no-op unless a handler is registered another way",
        Category,
        DiagnosticSeverity.Info,
        true,
        "Publishing a notification with no subscriber is legal but is often unintended.",
        customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

    public static readonly DiagnosticDescriptor PipelineExemptionClosedGeneric = new(
        "CQRA008",
        "PipelineExemption can use the open-generic form",
        "'{0}' is a closed generic; the open-generic form 'typeof({1})' exempts the same behavior for this request and is the idiomatic shorthand",
        Category,
        DiagnosticSeverity.Info,
        true,
        "A generic pipeline behavior is registered as an open generic and closed over each request, so on the request the attribute sits on, typeof(Behavior<,>) names the same behavior as the closed form; it is shorter and keeps working when the request's result type changes.");

    public static readonly DiagnosticDescriptor GeneratorNotRunning = new(
        "CQRA010",
        "CQRSharp handlers found but the source generator is not running in this project",
        "This project declares CQRSharp handlers, but the CQRSharp source generator is not running here, so no module is emitted and these handlers are never registered. Reference the CQRSharp package from this project.",
        Category,
        DiagnosticSeverity.Warning,
        true,
        "CQRSharp registers handlers only in assemblies where its source generator runs; each emits its own module. A project with handlers but without the generator emits no module, so dispatching its requests fails with \"no handler\". The generator ships in the CQRSharp package: reference it from every project that declares handlers.",
        customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

    public static readonly DiagnosticDescriptor MissingContextFactory = new(
        "CQRA011",
        "Custom request context has no registered factory",
        "Request '{0}' declares context type '{1}', but no IRequestContextFactory<{1}> is discoverable in this compilation or referenced assemblies; unless one is registered from a project this one cannot see, dispatching it throws at runtime. Implement and register a factory for '{1}', or use the default context (CommandBase, QueryBase<TResult>, ResultCommandBase<TResult> or StreamRequestBase<TItem>).",
        Category,
        DiagnosticSeverity.Warning,
        true,
        "A request deriving from CommandBase<TContext>, QueryBase<TResult, TContext>, ResultCommandBase<TResult, TContext> or StreamRequestBase<TItem, TContext> with a custom TContext needs an IRequestContextFactory<TContext>; without one the dispatcher throws 'No IRequestContextFactory ... is registered' on first dispatch. The generator auto-registers any factory it can see, so a discoverable factory type is enough.",
        customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

    public static readonly DiagnosticDescriptor TransactionalCommandOnNonCommand = new(
        "CQRA015",
        "ITransactionalCommand on a request that is not a command",
        "'{0}' implements ITransactionalCommand but is not a command (ICommand or ICommand<TResult>); it runs in a committed unit of work but is still dispatched as what it is. Make it a command, or mark a query or stream that writes with ITransactionalQuery and IsReadOnly = false.",
        Category,
        DiagnosticSeverity.Warning,
        true,
        "ITransactionalCommand opts a command into a unit-of-work transaction; it does not turn a request into a command. On a query or a stream it still commits the unit of work, while the request keeps its query or stream lifecycle notifications, span and metrics. ITransactionalQuery is the marker for a query or stream that runs in a transaction, with IsReadOnly = false when it writes.");

    public static readonly DiagnosticDescriptor ValidatorWithoutValidation = new(
        "CQRA012",
        "Validator declared but the validation behavior is not enabled",
        "'{0}' is an IRequestValidator<{1}>, but CQRSharp is registered here without the validation behavior, so '{1}' reaches its handler unvalidated. Register it with AddCqrsGenerated(), or AddCqrsGenerated(b => ...) without UseValidation(false), which add the validation behavior.",
        Category,
        DiagnosticSeverity.Warning,
        true,
        "AddCqrsGenerated() and every AddCqrsGenerated(b => ...) call add the validation behavior unless the configuration calls UseValidation(false); the core AddCqrs() adds no pipeline behavior at all. When every CQRSharp registration in this project leaves the validation behavior out, a validator is silently inert and invalid input reaches the handler.",
        customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

    public static readonly DiagnosticDescriptor RetryableRequestWithoutResilience = new(
        "CQRA019",
        "IRetryableRequest without UseResilience",
        "{0} implement IRetryableRequest, but this CQRSharp configuration never calls UseResilience(...), so no resilience behavior is registered and failures are not retried; the first dispatch logs CQRCONF006 at runtime. Call UseResilience(...) on the builder.",
        Category,
        DiagnosticSeverity.Warning,
        true,
        "IRetryableRequest opts a request into retries, which only the resilience behavior that UseResilience(...) registers performs; without it the request still runs once. Reported only when the application's whole CQRSharp configuration is in view: one AddCqrsGenerated call made of builder verbs, in an application no referenced assembly can configure further.",
        customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

    public static readonly DiagnosticDescriptor UnnamedHandledNotification = new(
        "CQRA020",
        "Handled notification is not durable under the outbox",
        "'{0}' has handlers but no [NotificationName], so while the outbox is on (UseOutbox) it is never stored in the outbox and every publish of it is delivered in-process. Add [NotificationName(\"...\")] to make it durable, or leave it as it is if it is meant to stay in-process.",
        Category,
        DiagnosticSeverity.Info,
        true,
        "The generated notification serializer names exactly the [NotificationName] notifications, and only a notification it names can be stored in the outbox; any other is dispatched in-process, which the startup validator and the first publish report as CQRCONF003. Reported only when the application's whole CQRSharp configuration is in view and nothing in view replaces the generated serializer or sets the outbox options outside the builder.",
        customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });
}
