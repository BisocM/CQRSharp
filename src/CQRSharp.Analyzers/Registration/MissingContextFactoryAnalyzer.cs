using System.Collections.Generic;
using System.Collections.Immutable;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CQRSharp.Analyzers;

/// <summary>
///     CQRA011: a concrete request declares a custom context (through <c>CommandBase&lt;TContext&gt;</c>,
///     <c>QueryBase&lt;TResult, TContext&gt;</c>, <c>ResultCommandBase&lt;TResult, TContext&gt;</c> or
///     <c>StreamRequestBase&lt;TItem, TContext&gt;</c> with a <c>TContext</c> other than <c>RequestContextBase</c>) but
///     no <c>IRequestContextFactory&lt;TContext&gt;</c> is discoverable — neither a factory implementation in this
///     compilation nor a generator-emitted <c>CqrsRegisteredContextFactory</c> marker in it or a referenced assembly.
///     Without a factory the dispatcher throws "No IRequestContextFactory ... is registered" on the first dispatch. The
///     generator auto-registers any factory it can see, so a discoverable factory type is sufficient coverage.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MissingContextFactoryAnalyzer : DiagnosticAnalyzer
{
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
        var defaultContext = known.RequestContextBase;
        var factoryInterface = known.IRequestContextFactory;
        var factoryMarker = known.CqrsRegisteredContextFactoryAttribute;
        // The factory interface lives in CQRSharp.Core: a project without it (a contracts project) declares requests
        // whose factory is registered where they are dispatched, so there is nothing to check here.
        if (requestMarker is null || defaultContext is null || factoryInterface is null || factoryMarker is null) return;

        var gate = new object();
        var coveredContexts = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var candidateRequests = new List<(string request, ITypeSymbol context, Location location)>();

        // Factories in referenced assemblies are advertised by the generator marker; collect them up front.
        foreach (var reference in context.Compilation.References)
            if (context.Compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly)
                CollectFactoryMarkers(assembly, factoryMarker, coveredContexts);

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
            if (!type.Implements(requestMarker)) return;
            var declaredContext = known.DeclaredContextOf(type);
            if (declaredContext is null || SymbolEqualityComparer.Default.Equals(declaredContext, defaultContext)) return;
            // A request generic over its context (Ping<TContext>) names no context until it is closed.
            if (CqrsKnownSymbols.ContainsTypeParameter(declaredContext)) return;

            if (type.FirstSourceLocation() is { } location)
                lock (gate) candidateRequests.Add((type.Name, declaredContext, location));
        }, SymbolKind.NamedType);

        context.RegisterCompilationEndAction(endContext =>
        {
            // The generator's marker for this very compilation covers a factory declared in generated code, which the
            // symbol action above does not see.
            CollectFactoryMarkers(endContext.Compilation.Assembly, factoryMarker, coveredContexts);

            foreach (var (request, declaredContext, location) in candidateRequests)
                if (!coveredContexts.Contains(declaredContext))
                    endContext.ReportDiagnostic(Diagnostic.Create(
                        CqrsDiagnostics.MissingContextFactory, location, request, declaredContext.Name));
        });
    }

    private static void CollectFactoryMarkers(IAssemblySymbol assembly, INamedTypeSymbol factoryMarker, HashSet<ITypeSymbol> covered)
    {
        foreach (var attribute in assembly.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, factoryMarker)) continue;
            if (attribute.ConstructorArguments.Length != 1) continue;

            var arg = attribute.ConstructorArguments[0];
            if (arg.Kind == TypedConstantKind.Type && arg.Value is ITypeSymbol contextType)
                covered.Add(contextType);
        }
    }
}
