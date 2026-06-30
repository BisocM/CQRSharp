using System.Collections.Immutable;
using System.Linq;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CQRSharp.Analyzers;

/// <summary>
///     CQRA017 (Info): a type implements both <c>IPreHandlerAttribute</c> and <c>IPostHandlerAttribute</c> directly
///     instead of the one-stop <c>ICommandInterceptor</c> (which combines them). Purely an ergonomics nudge toward the
///     idiomatic combined interface; nothing is broken.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CommandInterceptorGuidanceAnalyzer : DiagnosticAnalyzer
{
    private const string CommandInterceptorMetadataName = "CQRSharp.Abstractions.Attributes.Pipelines.ICommandInterceptor";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(CqrsDiagnostics.CollapseToCommandInterceptor);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var known = CqrsKnownSymbols.For(context.Compilation);
        var pre = known.IPreHandlerAttribute;
        var post = known.IPostHandlerAttribute;
        if (pre is null || post is null) return;

        // ICommandInterceptor already implies both; a type using it is the desired shape and must not be flagged.
        var commandInterceptor = context.Compilation.GetTypeByMetadataName(CommandInterceptorMetadataName);

        context.RegisterSymbolAction(symbolContext =>
        {
            var type = (INamedTypeSymbol)symbolContext.Symbol;
            if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct) || type.IsAbstract) return;

            var interfaces = type.AllInterfaces;
            var implementsPre = interfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, pre));
            var implementsPost = interfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, post));
            if (!implementsPre || !implementsPost) return;

            if (commandInterceptor is not null &&
                interfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, commandInterceptor)))
                return;

            var location = type.Locations.FirstOrDefault(l => l.IsInSource);
            if (location is not null)
                symbolContext.ReportDiagnostic(Diagnostic.Create(
                    CqrsDiagnostics.CollapseToCommandInterceptor, location, type.Name));
        }, SymbolKind.NamedType);
    }
}
