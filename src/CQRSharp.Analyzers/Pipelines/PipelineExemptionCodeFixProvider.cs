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
///     open-generic shorthand <c>typeof(Behavior&lt;,&gt;)</c>. CQRA008 is only reported for a closed form that exempts
///     the behavior on the request it sits on, when no other request can derive from that request and inherit the
///     exemption: only there do both forms exempt the same behavior.
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

        // The diagnostic span covers the whole typeof(...) expression; the behavior is the rightmost name of its type
        // (Namespace.Logging<A, B>), never a qualifier or a type argument.
        var typeOf = root.FindNode(context.Span, getInnermostNodeForTie: true)
            .DescendantNodesAndSelf()
            .OfType<TypeOfExpressionSyntax>()
            .FirstOrDefault();
        var named = typeOf?.Type switch
        {
            QualifiedNameSyntax qualified => qualified.Right,
            AliasQualifiedNameSyntax alias => alias.Name,
            SimpleNameSyntax simple => simple,
            _ => null
        };
        if (named is not GenericNameSyntax generic || generic.TypeArgumentList.Arguments.All(a => a is OmittedTypeArgumentSyntax)) return;

        var openForm = generic.Identifier.ValueText + "<" + new string(',', generic.TypeArgumentList.Arguments.Count - 1) + ">";
        context.RegisterCodeFix(
            CodeAction.Create(
                "Use the open-generic form typeof(" + openForm + ")",
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
