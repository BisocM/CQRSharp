using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CQRSharp.Analyzers;

/// <summary>
///     Code fix for CQRA008: rewrites a closed-generic <c>[PipelineExemption(typeof(Behavior&lt;A, B&gt;))]</c> to the
///     open-generic shorthand <c>typeof(Behavior&lt;,&gt;)</c>.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(PipelineExemptionCodeFixProvider))]
[Shared]
public sealed class PipelineExemptionCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(CqrsDiagnostics.PipelineExemptionClosedGeneric.Id);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        // The diagnostic span covers the whole typeof(...) expression; the behavior name is its outermost generic.
        var generic = root.FindNode(context.Span, getInnermostNodeForTie: true)
            .DescendantNodesAndSelf()
            .OfType<GenericNameSyntax>()
            .FirstOrDefault(g => g.TypeArgumentList.Arguments.Any(a => a is not OmittedTypeArgumentSyntax));
        if (generic is null) return;

        context.RegisterCodeFix(
            CodeAction.Create(
                "Use the open-generic form typeof(" + generic.Identifier.ValueText + "<,>)",
                _ => Task.FromResult(ToOpenGeneric(context.Document, root, generic)),
                nameof(PipelineExemptionCodeFixProvider)),
            context.Diagnostics);
    }

    private static Document ToOpenGeneric(Document document, SyntaxNode root, GenericNameSyntax generic)
    {
        var arity = generic.TypeArgumentList.Arguments.Count;

        // A SeparatedList of N omitted type arguments renders as N-1 commas between empty positions — i.e. "<,>" for
        // arity 2 — which is exactly the unbound-generic syntax. Each node must be a fresh instance.
        var omitted = SyntaxFactory.SeparatedList<TypeSyntax>(
            Enumerable.Range(0, arity).Select(_ => SyntaxFactory.OmittedTypeArgument()));

        var open = generic.WithTypeArgumentList(SyntaxFactory.TypeArgumentList(omitted));
        return document.WithSyntaxRoot(root.ReplaceNode(generic, open));
    }
}
