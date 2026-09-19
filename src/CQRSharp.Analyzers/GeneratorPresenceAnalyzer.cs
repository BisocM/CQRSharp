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
///     <c>[assembly: CqrsGeneratedModule]</c> marker). A handler-bearing project that references the framework but not
///     the generator emits no module, so its handlers are silently skipped — surfacing only as a runtime "no handler".
///     This analyzer ships with <c>CQRSharp.Abstractions</c> (which every handler project references), so it is present
///     even when the generator is not — and fires precisely when there are handlers but no generated-module marker.
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

        // The marker the generator emits whenever it runs in an assembly with handlers. If its type can't be resolved,
        // CQRSharp.Abstractions isn't referenced → this isn't a CQRSharp project, so there is nothing to check.
        var moduleAttribute = compilation.GetTypeByMetadataName(
            "CQRSharp.Abstractions.Attributes.SourceGeneration.CqrsGeneratedModuleAttribute");
        if (moduleAttribute is null) return;

        // Marker present ⇒ the generator IS running in this assembly ⇒ handlers are wired ⇒ nothing to warn about.
        if (compilation.Assembly.GetAttributes()
            .Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, moduleAttribute)))
            return;

        // Recognise the dispatch-handler interfaces via the well-known-type handoff (resolved from CQRSharp.Core, which
        // a handler project reaches at least transitively). If they don't resolve we can't identify handlers — stay
        // silent rather than guess.
        var known = CqrsKnownSymbols.For(compilation);
        var handlerDefinitions = new[]
            {
                known.ICommandHandler1, known.ICommandHandler2,
                known.IResultCommandHandler2, known.IResultCommandHandler3,
                known.IQueryHandler2, known.IQueryHandler3,
                known.IStreamRequestHandler2, known.IStreamRequestHandler3,
                known.INotificationHandler
            }
            .Where(s => s is not null)
            .Select(s => s!.OriginalDefinition)
            .ToImmutableArray();
        if (handlerDefinitions.Length == 0) return;

        // Collect the handler-implementing types declared here, then report once at the end. (The walk only happens in
        // a project that is MISSING the generator — the common case bails above at the marker check.)
        var handlerLocations = new ConcurrentBag<Location>();

        context.RegisterSymbolAction(symbolContext =>
        {
            if (symbolContext.Symbol is not INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } type)
                return;

            var implementsHandler = type.AllInterfaces.Any(iface =>
                iface.IsGenericType &&
                handlerDefinitions.Any(def => SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, def)));
            if (!implementsHandler) return;

            var location = type.Locations.FirstOrDefault(l => l.IsInSource);
            if (location is not null) handlerLocations.Add(location);
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
