using System.Collections.Immutable;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CQRSharp.Analyzers;

/// <summary>
///     CQRA005: flags <c>[PipelineExemption(typeof(T))]</c> where <c>T</c> does not implement a pipeline behavior
///     interface. Such an exemption silently has no effect — the behavior still runs.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PipelineExemptionAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(CqrsDiagnostics.PipelineExemptionNotBehavior);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            var known = CqrsKnownSymbols.For(start.Compilation);
            var exemption = known.PipelineExemptionAttribute;
            if (exemption is null) return;

            var behavior = known.IPipelineBehavior;
            var streamBehavior = known.IStreamPipelineBehavior;
            if (behavior is null && streamBehavior is null) return;

            start.RegisterSyntaxNodeAction(ctx => Analyze(ctx, exemption, behavior, streamBehavior), SyntaxKind.Attribute);
        });
    }

    private static void Analyze(SyntaxNodeAnalysisContext ctx, INamedTypeSymbol exemption, INamedTypeSymbol? behavior, INamedTypeSymbol? streamBehavior)
    {
        var attribute = (AttributeSyntax)ctx.Node;

        if (ctx.SemanticModel.GetSymbolInfo(attribute, ctx.CancellationToken).Symbol is not IMethodSymbol constructor)
            return;
        if (!SymbolEqualityComparer.Default.Equals(constructor.ContainingType, exemption)) return;
        if (attribute.ArgumentList is null) return;

        foreach (var argument in attribute.ArgumentList.Arguments)
        {
            if (argument.Expression is not TypeOfExpressionSyntax typeOf) continue;
            if (ctx.SemanticModel.GetTypeInfo(typeOf.Type, ctx.CancellationToken).Type is not INamedTypeSymbol exempted) continue;
            if (ImplementsBehavior(exempted, behavior, streamBehavior)) continue;

            ctx.ReportDiagnostic(Diagnostic.Create(CqrsDiagnostics.PipelineExemptionNotBehavior, typeOf.GetLocation(), exempted.Name));
        }
    }

    private static bool ImplementsBehavior(INamedTypeSymbol type, INamedTypeSymbol? behavior, INamedTypeSymbol? streamBehavior)
    {
        foreach (var iface in type.AllInterfaces)
        {
            var definition = iface.OriginalDefinition;
            if (behavior is not null && SymbolEqualityComparer.Default.Equals(definition, behavior)) return true;
            if (streamBehavior is not null && SymbolEqualityComparer.Default.Equals(definition, streamBehavior)) return true;
        }

        return false;
    }
}