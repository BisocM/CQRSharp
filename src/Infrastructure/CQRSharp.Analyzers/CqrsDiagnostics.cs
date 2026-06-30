using Microsoft.CodeAnalysis;

namespace CQRSharp.Analyzers;

/// <summary>
///     Diagnostic descriptors raised by the CQRSharp analyzers to help consumers use the framework correctly.
/// </summary>
internal static class CqrsDiagnostics
{
    public const string Category = "CQRSharp.Usage";

    public static readonly DiagnosticDescriptor SendStreamRequest = new(
        "CQRA004",
        "Stream request sent via Send",
        "'{0}' is a stream request and must be dispatched with Stream(...), not Send(...)",
        Category,
        DiagnosticSeverity.Error,
        true,
        "Sending an IStreamRequest through ICqrsDispatcher.Send compiles but throws at runtime; use Stream(...) instead.");

    public static readonly DiagnosticDescriptor PipelineExemptionNotBehavior = new(
        "CQRA005",
        "PipelineExemption target is not a pipeline behavior",
        "'{0}' is not a pipeline behavior, so [PipelineExemption(typeof({0}))] has no effect",
        Category,
        DiagnosticSeverity.Error,
        true,
        "PipelineExemption only suppresses types that implement IPipelineBehavior<,> or IStreamPipelineBehavior<,>.");

    public static readonly DiagnosticDescriptor PipelineExemptionClosedGeneric = new(
        "CQRA008",
        "PipelineExemption can use the open-generic form",
        "'{0}' is a closed generic; use the open-generic form 'typeof({1})' — it exempts the behavior for every request and is the idiomatic shorthand",
        Category,
        DiagnosticSeverity.Info,
        true,
        "A generic pipeline behavior is registered as an open generic and closed per request, so spelling out the type arguments in [PipelineExemption] is unnecessary; typeof(Behavior<,>) is the simpler, more robust form.");

    public static readonly DiagnosticDescriptor ValueCommandGuidance = new(
        "CQRA009",
        "Command returns data — confirm it cannot be queried",
        "'{0}' returns data from a command (ICommand<{1}>). Use a value-returning command only for a value no query could reproduce (a one-time secret or token); if '{1}' is persisted and queryable, model the read as IQuery<{1}> instead.",
        Category,
        DiagnosticSeverity.Info,
        true,
        "ICommand<TResult> deliberately relaxes the command/query split. It is intended only for a value minted at the instant of the operation that no query can return (a one-time API key, a generated token); for anything queryable, return a CommandResult and read the value with a query.");

    // The following are Warning/Info (not Error): the analyzer cannot be certain the code is broken because handlers
    // may be registered another way (manual DI), and a notification with no subscriber is legal.

    public static readonly DiagnosticDescriptor HandlerContextMismatch = new(
        "CQRA001",
        "Handler context type does not match the request",
        "Handler '{0}' uses context type '{1}', but request '{2}' declares context type '{3}'",
        Category,
        DiagnosticSeverity.Warning,
        true,
        "A handler's context type argument should match the context type the request declares (via RequestBase<TContext>).");

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

    public static readonly DiagnosticDescriptor RateLimitingContextMissing = new(
        "CQRA007",
        "Request context does not opt into rate limiting",
        "Rate limiting is configured, but request '{0}' declares context type '{1}', which does not implement CQRSharp.Pipelines.IRateLimitedContext, so the request is never rate limited",
        Category,
        DiagnosticSeverity.Warning,
        true,
        "The rate-limiting behavior only throttles a request whose context implements IRateLimitedContext (it supplies the user/request identifiers the limiter keys on); a request whose context does not implement it silently passes through unthrottled. Implement IRateLimitedContext on the context to opt in.",
        customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });
}