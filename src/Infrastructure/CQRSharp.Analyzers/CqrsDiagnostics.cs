using Microsoft.CodeAnalysis;

namespace CQRSharp.Analyzers;

/// <summary>
///     Diagnostic descriptors raised by the CQRSharp analyzers to help consumers use the framework correctly.
/// </summary>
internal static class CqrsDiagnostics
{
    public const string Category = "CQRSharp.Usage";

    public static readonly DiagnosticDescriptor SendStreamRequest = new(
        id: "CQRA004",
        title: "Stream request sent via Send",
        messageFormat: "'{0}' is a stream request and must be dispatched with Stream(...), not Send(...)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Sending an IStreamRequest through ICqrsDispatcher.Send compiles but throws at runtime; use Stream(...) instead.");

    public static readonly DiagnosticDescriptor PipelineExemptionNotBehavior = new(
        id: "CQRA005",
        title: "PipelineExemption target is not a pipeline behavior",
        messageFormat: "'{0}' is not a pipeline behavior, so [PipelineExemption(typeof({0}))] has no effect",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "PipelineExemption only suppresses types that implement IPipelineBehavior<,> or IStreamPipelineBehavior<,>.");

    // The following are Warning/Info (not Error): the analyzer cannot be certain the code is broken because handlers
    // may be registered another way (manual DI), and a notification with no subscriber is legal.

    public static readonly DiagnosticDescriptor HandlerContextMismatch = new(
        id: "CQRA001",
        title: "Handler context type does not match the request",
        messageFormat: "Handler '{0}' uses context type '{1}', but request '{2}' declares context type '{3}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A handler's context type argument should match the context type the request declares (via RequestBase<TContext>).");

    public static readonly DiagnosticDescriptor NoHandlerForRequest = new(
        id: "CQRA003",
        title: "No handler found for the dispatched request",
        messageFormat: "No handler was found for request '{0}' in this compilation or referenced assemblies; dispatching it will fail at runtime unless a handler is registered another way",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Send/Stream of a request with no discoverable handler throws at runtime.",
        customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

    public static readonly DiagnosticDescriptor NoSubscriberForNotification = new(
        id: "CQRA006",
        title: "No subscriber found for the published notification",
        messageFormat: "No handler was found for notification '{0}' in this compilation or referenced assemblies; the publish will be a no-op unless a handler is registered another way",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Publishing a notification with no subscriber is legal but is often unintended.",
        customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });
}
