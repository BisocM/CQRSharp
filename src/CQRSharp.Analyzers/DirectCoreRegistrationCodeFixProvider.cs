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
///     Code fix for CQRA014: replaces the offending <c>AddCqrs</c> call with <c>AddCqrsGenerated</c>. For the common
///     parameterless <c>AddCqrs()</c> it produces a working <c>AddCqrsGenerated()</c>; when the call passed the
///     core-only configuration delegates, the rename surfaces the right method and the compiler then guides the caller
///     to the builder lambda overload.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(DirectCoreRegistrationCodeFixProvider))]
[Shared]
public sealed class DirectCoreRegistrationCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(CqrsDiagnostics.DirectCoreRegistration.Id);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        var name = root.FindNode(context.Span, getInnermostNodeForTie: true)
            .FirstAncestorOrSelf<SimpleNameSyntax>();
        if (name is null || name.Identifier.ValueText != "AddCqrs") return;

        context.RegisterCodeFix(
            CodeAction.Create(
                "Use AddCqrsGenerated(...)",
                _ => Task.FromResult(ReplaceWithGenerated(context.Document, root, name)),
                "CQRA014_UseAddCqrsGenerated"),
            context.Diagnostics);
    }

    private static Document ReplaceWithGenerated(Document document, SyntaxNode root, SimpleNameSyntax name)
    {
        var renamed = name.WithIdentifier(SyntaxFactory.Identifier("AddCqrsGenerated").WithTriviaFrom(name.Identifier));
        return document.WithSyntaxRoot(root.ReplaceNode(name, renamed));
    }
}
