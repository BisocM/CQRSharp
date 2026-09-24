using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
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

        var notificationHandler = known.INotificationHandler;

        var handledRequests = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var handledNotifications = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        CollectReferencedMarkers(compilation, known, handledRequests, handledNotifications);

        var gate = new object();
        var sendSites = new List<(ITypeSymbol type, Location location)>();
        var publishSites = new List<(ITypeSymbol type, Location location)>();

        // Current-compilation handlers: add the request/notification each handler implementation handles. The
        // generator registers closed, non-abstract classes only, so an abstract handler base or an open-generic handler
        // handles nothing.
        context.RegisterSymbolAction(symbolContext =>
        {
            var type = (INamedTypeSymbol)symbolContext.Symbol;
            if (type is not { TypeKind: TypeKind.Class, IsAbstract: false, IsGenericType: false }) return;

            foreach (var iface in type.AllInterfaces)
            {
                if (!known.IsDispatchHandler(iface)) continue;

                var handled = WithoutTupleNames(iface.TypeArguments[0], compilation);
                var isNotificationHandler = SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, notificationHandler);
                lock (gate)
                {
                    (isNotificationHandler ? handledNotifications : handledRequests).Add(handled);
                }
            }
        }, SymbolKind.NamedType);

        // Dispatch sites: a concrete request/notification argument to Send/Stream/Publish on the dispatcher.
        context.RegisterOperationAction(opContext =>
        {
            var invocation = (IInvocationOperation)opContext.Operation;
            var method = invocation.TargetMethod;
            if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, dispatcher)) return;
            // The request is the first parameter, wherever named arguments put it in the call.
            var requestArgument = invocation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0);
            if (requestArgument is null) return;

            var argValue = requestArgument.Value;
            while (argValue is IConversionOperation conversion) argValue = conversion.Operand;
            if (argValue.Type is not INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false }) return;
            var argType = (INamedTypeSymbol)WithoutTupleNames(argValue.Type, compilation);

            var location = invocation.NameLocation();
            switch (method.Name)
            {
                case "Send" or "Stream" when argType.Implements(requestMarker):
                    lock (gate)
                    {
                        sendSites.Add((argType, location));
                    }

                    break;
                case "Publish" when argType.Implements(notificationMarker):
                    lock (gate)
                    {
                        publishSites.Add((argType, location));
                    }

                    break;
            }
        }, OperationKind.Invocation);

        context.RegisterCompilationEndAction(endContext =>
        {
            // The generator's markers for this very compilation: they cover a handler declared in generated code, which
            // the symbol action above does not see (generated code is not analyzed).
            CollectMarkers(compilation.Assembly, known, handledRequests, handledNotifications, compilation);

            foreach (var (type, location) in sendSites)
                if (!handledRequests.Contains(type))
                    endContext.ReportDiagnostic(Diagnostic.Create(CqrsDiagnostics.NoHandlerForRequest, location, type.Name));

            foreach (var (type, location) in publishSites)
                if (!IsSubscribed(type, handledNotifications))
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
            CollectMarkers(assembly, known, handledRequests, handledNotifications, compilation);
        }
    }

    private static void CollectMarkers(IAssemblySymbol assembly, CqrsKnownSymbols known, HashSet<ITypeSymbol> handledRequests,
        HashSet<ITypeSymbol> handledNotifications, Compilation compilation)
    {
        var handledRequestAttr = known.CqrsHandledRequestAttribute;
        var handledNotificationAttr = known.CqrsHandledNotificationAttribute;

        foreach (var attribute in assembly.GetAttributes())
        {
            var attrClass = attribute.AttributeClass;
            if (handledRequestAttr is not null && SymbolEqualityComparer.Default.Equals(attrClass, handledRequestAttr) && TryGetTypeArgument(attribute, out var request))
                handledRequests.Add(WithoutTupleNames(request!, compilation));
            else if (handledNotificationAttr is not null && SymbolEqualityComparer.Default.Equals(attrClass, handledNotificationAttr) &&
                     TryGetTypeArgument(attribute, out var notification))
                handledNotifications.Add(WithoutTupleNames(notification!, compilation));
        }
    }

    // Tuple element names are not part of the CLR type: Pair<(int A, int B)> and Pair<(int X, int Y)> are one request,
    // and a marker's typeof(...) carries no names at all. Compare with the names stripped; a type without a tuple
    // anywhere in it is returned as it is.
    private static ITypeSymbol WithoutTupleNames(ITypeSymbol type, Compilation compilation)
        => ContainsTuple(type) ? Rebuild(type, compilation) : type;

    private static bool ContainsTuple(ITypeSymbol type)
        => type switch
        {
            INamedTypeSymbol { IsTupleType: true } => true,
            INamedTypeSymbol named => named.TypeArguments.Any(ContainsTuple) || (named.ContainingType is { } outer && ContainsTuple(outer)),
            IArrayTypeSymbol array => ContainsTuple(array.ElementType),
            _ => false
        };

    private static ITypeSymbol Rebuild(ITypeSymbol type, Compilation compilation)
    {
        switch (type)
        {
            case INamedTypeSymbol { IsTupleType: true, TupleUnderlyingType: { } underlying }:
                return Rebuild(underlying, compilation);
            // IsGenericType is also true for a non-generic type nested in a constructed generic one (Outer<int>.Ping):
            // rebuild the container, and construct only a type that has type parameters of its own.
            case INamedTypeSymbol { IsGenericType: true, IsUnboundGenericType: false } named:
                var containing = named.ContainingType is { } outer ? (INamedTypeSymbol)Rebuild(outer, compilation) : null;
                var definition = containing?.GetTypeMembers(named.Name, named.Arity).FirstOrDefault() ?? named.OriginalDefinition;
                return named.Arity > 0
                    ? definition.Construct(named.TypeArguments.Select(a => Rebuild(a, compilation)).ToArray())
                    : definition;
            case IArrayTypeSymbol array:
                return compilation.CreateArrayTypeSymbol(Rebuild(array.ElementType, compilation), array.Rank);
            default:
                return type;
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

    // A notification reaches every handler of its own type, of a base type or of an interface of it, in-process and
    // through the outbox alike.
    private static bool IsSubscribed(ITypeSymbol notificationType, HashSet<ITypeSymbol> handledNotifications)
    {
        for (var current = notificationType; current is not null; current = current.BaseType)
            if (handledNotifications.Contains(current))
                return true;

        foreach (var iface in notificationType.AllInterfaces)
            if (handledNotifications.Contains(iface))
                return true;

        return false;
    }
}
