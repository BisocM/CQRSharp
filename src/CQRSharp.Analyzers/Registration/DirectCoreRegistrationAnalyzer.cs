using System.Collections.Immutable;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CQRSharp.Analyzers;

/// <summary>
///     CQRA014: flags a direct call to the low-level <c>AddCqrs()</c> core registration in user code. It registers
///     the dispatcher but not the source-generated handler routing, so the first dispatch throws at runtime. The only
///     legitimate callers are the fluent builder and the generated bootstrap, neither of which is analyzed user source,
///     so any call the analyzer sees is a mistake. The fix is <c>AddCqrsGenerated</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DirectCoreRegistrationAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(CqrsDiagnostics.DirectCoreRegistration);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            // The call is recognized by the method's containing type, so the generated AddCqrsGenerated overloads,
            // which live elsewhere, are never flagged.
            var extensions = CqrsKnownSymbols.For(start.Compilation).DependencyInjectionExtensions;
            if (extensions is null) return;
            start.RegisterOperationAction(ctx => Analyze(ctx, extensions), OperationKind.Invocation);
        });
    }

    private static void Analyze(OperationAnalysisContext ctx, INamedTypeSymbol extensions)
    {
        var invocation = (IInvocationOperation)ctx.Operation;
        var method = invocation.TargetMethod;
        if (method.Name != "AddCqrs") return;
        if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, extensions)) return;

        ctx.ReportDiagnostic(Diagnostic.Create(CqrsDiagnostics.DirectCoreRegistration, invocation.NameLocation()));
    }
}
