using System.Collections.Generic;
using System.Collections.Immutable;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CQRSharp.Analyzers;

/// <summary>
///     CQRA007: when rate limiting is configured somewhere in the compilation, flags concrete request types whose
///     declared context does not implement <c>CQRSharp.Pipelines.IRateLimitedContext</c>. The rate-limiting opt-in is
///     non-obvious — the behavior only throttles requests whose context implements that interface — so such a request
///     silently passes through unthrottled. The analyzer is deliberately conservative: it warns only when rate limiting
///     is actually configured AND the request clearly declares a context (via <c>RequestBase&lt;TContext&gt;</c>) that
///     lacks the interface, to avoid noise on apps that do not use rate limiting or whose context cannot be resolved.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RateLimitingContextAnalyzer : DiagnosticAnalyzer
{
    // The sanctioned hardcoded names: the rate-limiting context interface (Pipelines is not part of the well-known-type
    // metadata Core emits) and its obsolete pre-3.1.0 shim, both of which count as opting in.
    private const string RateLimitedContextMetadataName = "CQRSharp.Pipelines.IRateLimitedContext";
    private const string ObsoleteRateLimitedContextMetadataName =
        "CQRSharp.Pipelines.Behaviors.RateLimiting.Context.IRateLimitedContext";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(CqrsDiagnostics.RateLimitingContextMissing);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var known = CqrsKnownSymbols.For(context.Compilation);
        var requestMarker = known.IRequest;
        var requestBase = known.RequestBaseGeneric;
        if (requestMarker is null || requestBase is null) return;

        // The interface that opts a request in. If neither it nor its obsolete shim is resolvable, the consumer cannot
        // even reference rate limiting, so there is nothing to flag.
        var rateLimitedContext = context.Compilation.GetTypeByMetadataName(RateLimitedContextMetadataName);
        var obsoleteRateLimitedContext = context.Compilation.GetTypeByMetadataName(ObsoleteRateLimitedContextMetadataName);
        if (rateLimitedContext is null && obsoleteRateLimitedContext is null) return;

        // Whether rate limiting is configured is a whole-compilation fact, so collect candidates during the scan and
        // decide at CompilationEnd. A single-element array carries the flag across the concurrent analysis actions
        // (a captured local cannot be mutated from another action; the element is written under the gate).
        var rateLimitingConfigured = new bool[1];
        var gate = new object();
        var candidates = new List<(INamedTypeSymbol request, ITypeSymbol context)>();

        // Rate limiting is opted in via an AddRateLimiting/UseRateLimiting call or a pack ConfigureRateLimiting setter.
        context.RegisterOperationAction(opContext =>
        {
            var configured = opContext.Operation switch
            {
                IInvocationOperation invocation => invocation.TargetMethod.Name is "AddRateLimiting" or "UseRateLimiting",
                IAssignmentOperation { Target: IPropertyReferenceOperation property } =>
                    property.Property.Name == "ConfigureRateLimiting",
                _ => false
            };

            if (configured)
                lock (gate) rateLimitingConfigured[0] = true;
        }, OperationKind.Invocation, OperationKind.SimpleAssignment);

        // Concrete request types whose declared context lacks the interface are the candidates to flag.
        context.RegisterSymbolAction(symbolContext =>
        {
            var type = (INamedTypeSymbol)symbolContext.Symbol;
            if (type.TypeKind != TypeKind.Class || type.IsAbstract) return;
            if (!Implements(type, requestMarker)) return;

            var declaredContext = CqrsContextResolution.GetDeclaredContext(type, requestBase);
            if (declaredContext is null) return; // context cannot be resolved statically — stay conservative
            if (ImplementsRateLimited(declaredContext, rateLimitedContext, obsoleteRateLimitedContext)) return;

            lock (gate) candidates.Add((type, declaredContext));
        }, SymbolKind.NamedType);

        context.RegisterCompilationEndAction(endContext =>
        {
            if (!rateLimitingConfigured[0]) return;

            foreach (var (request, declaredContext) in candidates)
            {
                var location = request.Locations.Length > 0 ? request.Locations[0] : Location.None;
                endContext.ReportDiagnostic(Diagnostic.Create(
                    CqrsDiagnostics.RateLimitingContextMissing, location, request.Name, declaredContext.Name));
            }
        });
    }

    private static bool Implements(ITypeSymbol type, INamedTypeSymbol iface)
    {
        foreach (var implemented in type.AllInterfaces)
            if (SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, iface))
                return true;
        return false;
    }

    private static bool ImplementsRateLimited(ITypeSymbol context, INamedTypeSymbol? rateLimited, INamedTypeSymbol? obsolete)
    {
        if (rateLimited is not null && SymbolEqualityComparer.Default.Equals(context, rateLimited)) return true;
        if (obsolete is not null && SymbolEqualityComparer.Default.Equals(context, obsolete)) return true;

        foreach (var iface in context.AllInterfaces)
        {
            if (rateLimited is not null && SymbolEqualityComparer.Default.Equals(iface, rateLimited)) return true;
            if (obsolete is not null && SymbolEqualityComparer.Default.Equals(iface, obsolete)) return true;
        }

        return false;
    }
}
