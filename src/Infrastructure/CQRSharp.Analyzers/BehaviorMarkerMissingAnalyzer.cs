using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CQRSharp.Analyzers;

/// <summary>
///     CQRA013 (Info): when resilience or idempotency is configured somewhere in the compilation, points out concrete
///     requests that do not opt into it (via <c>IRetryableRequest</c> / <c>IIdempotentRequest</c>) and are therefore
///     silently passed through un-retried / un-deduplicated. Informational because opting a request out is frequently
///     intentional (at-most-once is a deliberate safety gate). Mirrors CQRA007's "configured-but-not-opted-in" shape.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BehaviorMarkerMissingAnalyzer : DiagnosticAnalyzer
{
    // The opt-in markers live in CQRSharp.Abstractions (not part of the well-known-type metadata), so resolve by name.
    private const string RetryableMetadataName = "CQRSharp.Abstractions.Interfaces.Markers.Request.IRetryableRequest";
    private const string IdempotentMetadataName = "CQRSharp.Abstractions.Interfaces.Markers.Request.IIdempotentRequest";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(CqrsDiagnostics.BehaviorMarkerMissing);

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
        if (requestMarker is null) return;

        var retryable = context.Compilation.GetTypeByMetadataName(RetryableMetadataName);
        var idempotent = context.Compilation.GetTypeByMetadataName(IdempotentMetadataName);
        if (retryable is null && idempotent is null) return;

        var resilienceConfigured = new bool[1];
        var idempotencyConfigured = new bool[1];
        var gate = new object();
        var requests = new List<INamedTypeSymbol>();

        context.RegisterOperationAction(op =>
        {
            switch (op.Operation)
            {
                case IInvocationOperation invocation:
                    switch (invocation.TargetMethod.Name)
                    {
                        case "UseResilience" or "AddResilience": lock (gate) resilienceConfigured[0] = true; break;
                        case "UseIdempotency" or "AddIdempotency": lock (gate) idempotencyConfigured[0] = true; break;
                    }

                    break;
                case IAssignmentOperation { Target: IPropertyReferenceOperation { Property.Name: "ConfigureResilience" } }:
                    lock (gate) resilienceConfigured[0] = true;
                    break;
            }
        }, OperationKind.Invocation, OperationKind.SimpleAssignment);

        context.RegisterSymbolAction(symbolContext =>
        {
            var type = (INamedTypeSymbol)symbolContext.Symbol;
            if (type.TypeKind != TypeKind.Class || type.IsAbstract) return;
            if (!type.Locations.Any(l => l.IsInSource)) return;
            if (!Implements(type, requestMarker)) return;
            lock (gate) requests.Add(type);
        }, SymbolKind.NamedType);

        context.RegisterCompilationEndAction(endContext =>
        {
            if (!resilienceConfigured[0] && !idempotencyConfigured[0]) return;

            foreach (var request in requests)
            {
                var location = request.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None;

                if (resilienceConfigured[0] && retryable is not null && !Implements(request, retryable))
                    endContext.ReportDiagnostic(Diagnostic.Create(
                        CqrsDiagnostics.BehaviorMarkerMissing, location,
                        "Resilience (retry)", request.Name, "IRetryableRequest", "retried"));

                if (idempotencyConfigured[0] && idempotent is not null && !Implements(request, idempotent))
                    endContext.ReportDiagnostic(Diagnostic.Create(
                        CqrsDiagnostics.BehaviorMarkerMissing, location,
                        "Idempotency", request.Name, "IIdempotentRequest", "deduplicated"));
            }
        });
    }

    private static bool Implements(ITypeSymbol type, INamedTypeSymbol iface)
    {
        if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, iface)) return true;
        foreach (var implemented in type.AllInterfaces)
            if (SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, iface))
                return true;
        return false;
    }
}
