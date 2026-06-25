using System.Collections.Generic;
using System.Collections.Immutable;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CQRSharp.Analyzers;

/// <summary>
///     CQRA003: <c>Send</c>/<c>Stream</c> of a request that has no discoverable handler. CQRA006: <c>Publish</c> of a
///     notification with no discoverable subscriber. Handlers are discovered from the current compilation (handler
///     implementations) and from referenced assemblies (via the generator-emitted assembly markers).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HandlerDiscoveryAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(CqrsDiagnostics.NoHandlerForRequest, CqrsDiagnostics.NoSubscriberForNotification);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var compilation = context.Compilation;
        var known = CqrsKnownSymbols.For(compilation);
        var dispatcher = known.Dispatcher;
        var requestMarker = known.IRequest;
        var notificationMarker = known.INotification;
        if (dispatcher is null || requestMarker is null || notificationMarker is null) return;

        // The resolver already stores the OriginalDefinition for generic roles.
        var commandHandler = known.ICommandHandler2;
        var queryHandler = known.IQueryHandler3;
        var streamHandler = known.IStreamRequestHandler3;
        var notificationHandler = known.INotificationHandler;

        var handledRequests = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var handledNotifications = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        CollectReferencedMarkers(compilation, known, handledRequests, handledNotifications);

        var gate = new object();
        var sendSites = new List<(ITypeSymbol type, Location location)>();
        var publishSites = new List<(ITypeSymbol type, Location location)>();

        // Current-compilation handlers: add the request/notification each handler implementation handles.
        context.RegisterSymbolAction(symbolContext =>
        {
            var type = (INamedTypeSymbol)symbolContext.Symbol;
            if (type.TypeKind != TypeKind.Class) return;

            foreach (var iface in type.AllInterfaces)
            {
                if (!iface.IsGenericType) continue;
                var def = iface.OriginalDefinition;

                if (IsAny(def, commandHandler, queryHandler, streamHandler))
                {
                    lock (gate) handledRequests.Add(iface.TypeArguments[0]);
                }
                else if (notificationHandler is not null && SymbolEqualityComparer.Default.Equals(def, notificationHandler))
                {
                    lock (gate) handledNotifications.Add(iface.TypeArguments[0]);
                }
            }
        }, SymbolKind.NamedType);

        // Dispatch sites: a concrete request/notification argument to Send/Stream/Publish on the dispatcher.
        context.RegisterOperationAction(opContext =>
        {
            var invocation = (IInvocationOperation)opContext.Operation;
            var method = invocation.TargetMethod;
            if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, dispatcher)) return;
            if (invocation.Arguments.Length == 0) return;

            var argValue = invocation.Arguments[0].Value;
            while (argValue is IConversionOperation conversion) argValue = conversion.Operand;
            if (argValue.Type is not INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } argType) return;

            var location = NameLocation(invocation);
            switch (method.Name)
            {
                case "Send" or "Stream" when Implements(argType, requestMarker):
                    lock (gate) sendSites.Add((argType, location));
                    break;
                case "Publish" when Implements(argType, notificationMarker):
                    lock (gate) publishSites.Add((argType, location));
                    break;
            }
        }, OperationKind.Invocation);

        context.RegisterCompilationEndAction(endContext =>
        {
            foreach (var (type, location) in sendSites)
                if (!handledRequests.Contains(type))
                    endContext.ReportDiagnostic(Diagnostic.Create(CqrsDiagnostics.NoHandlerForRequest, location, type.Name));

            foreach (var (type, location) in publishSites)
                if (!handledNotifications.Contains(type))
                    endContext.ReportDiagnostic(Diagnostic.Create(CqrsDiagnostics.NoSubscriberForNotification, location, type.Name));
        });
    }

    private static void CollectReferencedMarkers(Compilation compilation, CqrsKnownSymbols known, HashSet<ITypeSymbol> handledRequests, HashSet<ITypeSymbol> handledNotifications)
    {
        var handledRequestAttr = known.CqrsHandledRequestAttribute;
        var handledNotificationAttr = known.CqrsHandledNotificationAttribute;
        if (handledRequestAttr is null && handledNotificationAttr is null) return;

        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly) continue;

            foreach (var attribute in assembly.GetAttributes())
            {
                var attrClass = attribute.AttributeClass;
                if (handledRequestAttr is not null && SymbolEqualityComparer.Default.Equals(attrClass, handledRequestAttr) && TryGetTypeArgument(attribute, out var request))
                    handledRequests.Add(request!);
                else if (handledNotificationAttr is not null && SymbolEqualityComparer.Default.Equals(attrClass, handledNotificationAttr) && TryGetTypeArgument(attribute, out var notification))
                    handledNotifications.Add(notification!);
            }
        }
    }

    private static bool TryGetTypeArgument(AttributeData attribute, out ITypeSymbol? type)
    {
        type = null;
        if (attribute.ConstructorArguments.Length != 1) return false;
        var arg = attribute.ConstructorArguments[0];
        if (arg.Kind != TypedConstantKind.Type || arg.Value is not ITypeSymbol resolved) return false;
        type = resolved;
        return true;
    }

    private static bool IsAny(INamedTypeSymbol def, params INamedTypeSymbol?[] candidates)
    {
        foreach (var candidate in candidates)
            if (candidate is not null && SymbolEqualityComparer.Default.Equals(def, candidate)) return true;
        return false;
    }

    private static bool Implements(ITypeSymbol type, INamedTypeSymbol iface)
    {
        foreach (var implemented in type.AllInterfaces)
            if (SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, iface)) return true;
        return false;
    }

    private static Location NameLocation(IInvocationOperation invocation)
        => invocation.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess }
            ? memberAccess.Name.GetLocation()
            : invocation.Syntax.GetLocation();
}
