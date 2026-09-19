using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CQRSharp.Analyzers;

/// <summary>
///     CQRA011: a concrete request declares a custom context (via <c>CommandBase&lt;TContext&gt;</c> /
///     <c>QueryBase&lt;TContext&gt;</c> with a <c>TContext</c> other than <c>RequestContextBase</c>) but no
///     <c>IRequestContextFactory&lt;TContext&gt;</c> is discoverable — neither a factory implementation in this
///     compilation nor a generator-emitted <c>CqrsRegisteredContextFactory</c> marker in a referenced assembly. Without
///     a factory the dispatcher throws "No IRequestContextFactory ... is registered" on the first dispatch. The
///     generator auto-registers any factory it can see, so a discoverable factory type is sufficient coverage.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MissingContextFactoryAnalyzer : DiagnosticAnalyzer
{
    private const string ContextFactoryMarkerMetadataName =
        "CQRSharp.Abstractions.Attributes.SourceGeneration.CqrsRegisteredContextFactoryAttribute";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(CqrsDiagnostics.MissingContextFactory);

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
        var defaultContext = known.RequestContextBase;
        var factoryInterface = known.IRequestContextFactory;
        if (requestMarker is null || requestBase is null || defaultContext is null || factoryInterface is null) return;

        var gate = new object();
        var coveredContexts = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var candidateRequests = new List<(string request, ITypeSymbol context, Location location)>();

        // Factories in referenced assemblies are advertised by the generator marker; collect them up front.
        CollectReferencedFactoryMarkers(context.Compilation, coveredContexts, gate);

        context.RegisterSymbolAction(symbolContext =>
        {
            var type = (INamedTypeSymbol)symbolContext.Symbol;
            if (type.TypeKind != TypeKind.Class || type.IsAbstract) return;

            // (a) A factory implementation in this compilation covers the context it produces.
            foreach (var iface in type.AllInterfaces)
            {
                if (!iface.IsGenericType || iface.TypeArguments.Length == 0) continue;
                if (!SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, factoryInterface)) continue;
                lock (gate) coveredContexts.Add(iface.TypeArguments[0]);
            }

            // (b) A concrete request whose declared context is a custom (non-default) type is a candidate.
            if (!Implements(type, requestMarker)) return;
            var declaredContext = CqrsContextResolution.GetDeclaredContext(type, requestBase);
            if (declaredContext is null || SymbolEqualityComparer.Default.Equals(declaredContext, defaultContext)) return;

            var location = type.Locations.FirstOrDefault(l => l.IsInSource);
            if (location is not null)
                lock (gate) candidateRequests.Add((type.Name, declaredContext, location));
        }, SymbolKind.NamedType);

        context.RegisterCompilationEndAction(endContext =>
        {
            foreach (var (request, declaredContext, location) in candidateRequests)
                if (!coveredContexts.Contains(declaredContext))
                    endContext.ReportDiagnostic(Diagnostic.Create(
                        CqrsDiagnostics.MissingContextFactory, location, request, declaredContext.Name));
        });
    }

    private static void CollectReferencedFactoryMarkers(Compilation compilation, HashSet<ITypeSymbol> covered, object gate)
    {
        var markerAttribute = compilation.GetTypeByMetadataName(ContextFactoryMarkerMetadataName);
        if (markerAttribute is null) return;

        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly) continue;

            foreach (var attribute in assembly.GetAttributes())
            {
                if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, markerAttribute)) continue;
                if (attribute.ConstructorArguments.Length != 1) continue;

                var arg = attribute.ConstructorArguments[0];
                if (arg.Kind == TypedConstantKind.Type && arg.Value is ITypeSymbol contextType)
                    lock (gate) covered.Add(contextType);
            }
        }
    }

    private static bool Implements(ITypeSymbol type, INamedTypeSymbol iface)
    {
        foreach (var implemented in type.AllInterfaces)
            if (SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, iface))
                return true;
        return false;
    }
}
