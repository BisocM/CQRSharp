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
        ImmutableArray.Create(CqrsDiagnostics.PipelineExemptionNotBehavior, CqrsDiagnostics.PipelineExemptionClosedGeneric);

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

            // typeof(Behavior<,>) yields an UNBOUND generic whose AllInterfaces is empty; check its definition so an
            // open-generic behavior exemption (the idiomatic way to exempt a generic behavior) is recognized.
            var exemptedDefinition = exempted.IsUnboundGenericType ? exempted.OriginalDefinition : exempted;
            if (!ImplementsBehavior(exemptedDefinition, behavior, streamBehavior))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(CqrsDiagnostics.PipelineExemptionNotBehavior, typeOf.GetLocation(), exempted.Name));
                continue;
            }

            // CQRA008: a valid behavior exemption that names a CLOSED constructed generic (e.g.
            // typeof(Logging<Ping, CommandResult>)). Suggest the open-generic shorthand typeof(Logging<,>), which
            // exempts the behavior for every request the behavior is closed over.
            if (exempted.IsGenericType && !exempted.IsUnboundGenericType)
            {
                var openForm = exempted.Name + "<" + new string(',', exempted.TypeArguments.Length - 1) + ">";
                var closedForm = exempted.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                ctx.ReportDiagnostic(Diagnostic.Create(
                    CqrsDiagnostics.PipelineExemptionClosedGeneric, typeOf.GetLocation(), closedForm, openForm));
            }
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