using System.Collections.Immutable;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CQRSharp.Analyzers;

/// <summary>
///     Code fix for CQRA004: replaces the offending <c>Send</c> call with <c>Stream</c>.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(SendStreamRequestCodeFixProvider))]
[Shared]
public sealed class SendStreamRequestCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(CqrsDiagnostics.SendStreamRequest.Id);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        var name = root.FindNode(context.Span, getInnermostNodeForTie: true)
            .FirstAncestorOrSelf<SimpleNameSyntax>();
        if (name is null || name.Identifier.ValueText != "Send") return;

        context.RegisterCodeFix(
            CodeAction.Create(
                "Use Stream(...)",
                _ => Task.FromResult(ReplaceWithStream(context.Document, root, name)),
                "CQRA004_UseStream"),
            context.Diagnostics);
    }

    private static Document ReplaceWithStream(Document document, SyntaxNode root, SimpleNameSyntax name)
    {
        var renamed = name.WithIdentifier(SyntaxFactory.Identifier("Stream").WithTriviaFrom(name.Identifier));
        return document.WithSyntaxRoot(root.ReplaceNode(name, renamed));
    }
}