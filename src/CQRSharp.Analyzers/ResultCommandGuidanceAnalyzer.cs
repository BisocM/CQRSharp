using System.Collections.Immutable;
using System.Linq;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CQRSharp.Analyzers;

/// <summary>
///     CQRA009: an informational nudge on every concrete type that implements <c>ICommand&lt;TResult&gt;</c>. A
///     value-returning command deliberately relaxes the command/query split, so this reminds the author it is meant
///     only for a value no query could reproduce — and to use <c>IQuery&lt;TResult&gt;</c> for anything queryable. It
///     cannot (and does not try to) decide whether the value is queryable; it surfaces the choice at the declaration.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ResultCommandGuidanceAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(CqrsDiagnostics.ValueCommandGuidance);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            var commandWithResult = CqrsKnownSymbols.For(start.Compilation).ICommandWithResult;
            if (commandWithResult is null) return;

            start.RegisterSymbolAction(ctx => Analyze(ctx, commandWithResult), SymbolKind.NamedType);
        });
    }

    private static void Analyze(SymbolAnalysisContext ctx, INamedTypeSymbol commandWithResult)
    {
        if (ctx.Symbol is not INamedTypeSymbol type || type.IsAbstract) return;

        // Fire only on a concrete type that actually implements ICommand<TResult> (directly or via a base such as
        // ResultCommandBase<TResult>); skip the framework's own abstract bases and any other type.
        var iface = type.AllInterfaces.FirstOrDefault(i =>
            i.IsGenericType && SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, commandWithResult));
        if (iface is null || iface.TypeArguments.Length != 1) return;

        var location = type.Locations.FirstOrDefault(l => l.IsInSource);
        if (location is null) return;

        var resultName = iface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        ctx.ReportDiagnostic(Diagnostic.Create(CqrsDiagnostics.ValueCommandGuidance, location, type.Name, resultName));
    }
}
