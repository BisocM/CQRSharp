using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CQRSharp.Analyzers;

/// <summary>
///     CQRA010: warns when a project declares CQRSharp handlers but the source generator is not running in it. The
///     generator registers handlers only in assemblies where it runs (each emits its own module + a
///     <c>[assembly: CqrsGeneratedModule]</c> marker). A handler-bearing project without the generator emits no module,
///     so its handlers are silently skipped, surfacing only as a runtime "no handler". The handler interfaces live in
///     CQRSharp.Abstractions, so this check works in a project that references nothing else, the usual shape of one that
///     is missing the generator.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GeneratorPresenceAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(CqrsDiagnostics.GeneratorNotRunning);

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

        // The marker the generator emits whenever it runs in an assembly with handlers. If its type can't be resolved,
        // CQRSharp.Abstractions isn't referenced, so this isn't a CQRSharp project and there is nothing to check.
        var moduleAttribute = known.CqrsGeneratedModuleAttribute;
        if (moduleAttribute is null || known.DispatchHandlerDefinitions.IsEmpty) return;

        // Marker present: the generator IS running in this assembly and its handlers are wired.
        if (compilation.Assembly.GetAttributes()
            .Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, moduleAttribute)))
            return;

        // The marker is only emitted for an assembly with registerable content. The bootstrap is emitted for every
        // CQRSharp.Core-referencing assembly the generator runs in, so its presence proves the generator ran even when
        // every handler here was rejected (CQRGEN006/CQRGEN009) - a case for those diagnostics, not this one.
        if (compilation.Assembly.GetTypeByMetadataName("CQRSharp." + CqrsKnownSymbols.BootstrapTypeName) is not null ||
            compilation.Assembly.GetTypeByMetadataName(CqrsKnownSymbols.ModuleNamespaceFor(compilation.AssemblyName) + "." + CqrsKnownSymbols.BootstrapTypeName) is not null)
            return;

        // Collect the handler-implementing types declared here, then report once at the end. (The walk only happens in
        // a project that is MISSING the generator — the common case bails above at the marker check.)
        var handlerLocations = new ConcurrentBag<Location>();

        context.RegisterSymbolAction(symbolContext =>
        {
            if (symbolContext.Symbol is not INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } type)
                return;
            if (!type.AllInterfaces.Any(known.IsDispatchHandler)) return;

            if (type.FirstSourceLocation() is { } location) handlerLocations.Add(location);
        }, SymbolKind.NamedType);

        context.RegisterCompilationEndAction(endContext =>
        {
            // It's a project-level problem, but anchoring it to the first handler (deterministic order) makes it visible
            // in the editor rather than only in the build log.
            var first = handlerLocations
                .OrderBy(l => l.GetLineSpan().Path, System.StringComparer.Ordinal)
                .ThenBy(l => l.SourceSpan.Start)
                .FirstOrDefault();
            if (first is not null)
                endContext.ReportDiagnostic(Diagnostic.Create(CqrsDiagnostics.GeneratorNotRunning, first));
        });
    }
}
