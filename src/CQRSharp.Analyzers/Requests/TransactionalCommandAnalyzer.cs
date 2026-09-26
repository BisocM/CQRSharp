using System.Collections.Immutable;
using System.Linq;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CQRSharp.Analyzers;

/// <summary>
///     CQRA015: a concrete type implements <c>ITransactionalCommand</c> but not <c>ICommand</c> or <c>ICommand&lt;T&gt;</c>.
///     The marker only opts a command into a transaction, so on anything else it commits a unit of work for a request the
///     rest of the pipeline treats as the query or stream it is. An abstract base is not reported: a command derived from
///     it is legitimate, and each concrete type is judged on its own.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TransactionalCommandAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(CqrsDiagnostics.TransactionalCommandOnNonCommand);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var known = CqrsKnownSymbols.For(start.Compilation);
            if (known.ITransactionalCommand is not { } marker || known.ICommand is not { } command ||
                known.ICommandWithResult is not { } commandWithResult)
                return;

            start.RegisterSymbolAction(symbolContext =>
            {
                var type = (INamedTypeSymbol)symbolContext.Symbol;
                if (type.IsAbstract || type.TypeKind is not (TypeKind.Class or TypeKind.Struct)) return;

                var interfaces = type.AllInterfaces;
                if (!interfaces.Contains(marker, SymbolEqualityComparer.Default)) return;
                if (interfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, command) ||
                                        SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, commandWithResult)))
                    return;

                symbolContext.ReportDiagnostic(Diagnostic.Create(
                    CqrsDiagnostics.TransactionalCommandOnNonCommand, type.Locations.FirstOrDefault(), type.Name));
            }, SymbolKind.NamedType);
        });
    }
}
