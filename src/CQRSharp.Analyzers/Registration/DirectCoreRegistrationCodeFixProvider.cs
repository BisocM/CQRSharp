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
///     Code fix for CQRA014: calls the generated <c>AddCqrsGenerated()</c> instead of <c>AddCqrs()</c>; both take only the
///     service collection. A static-form call (<c>DependencyInjectionExtensions.AddCqrs(services)</c>, or
///     <c>AddCqrs(services)</c> through a <c>using static</c>) becomes the extension form on the service collection,
///     because the generated method lives on another type. The fix is withheld when the rewritten call does not bind to
///     a generated <c>AddCqrsGenerated</c>, for example where the generator has not produced the bootstrap.
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
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null || model is null) return;

        var invocation = root.FindNode(context.Span, getInnermostNodeForTie: true).FirstAncestorOrSelf<InvocationExpressionSyntax>();
        if (invocation is null || model.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol { Name: "AddCqrs" } method)
            return;

        var replacement = Rewrite(invocation, method);
        if (replacement is null) return;

        // Without arguments the rewritten call can only bind to the parameterless AddCqrsGenerated, when there is one.
        var bound = model.GetSpeculativeSymbolInfo(invocation.SpanStart, replacement, SpeculativeBindingOption.BindAsExpression).Symbol;
        if (bound is not IMethodSymbol { Name: "AddCqrsGenerated" }) return;

        context.RegisterCodeFix(
            CodeAction.Create(
                "Use AddCqrsGenerated()",
                _ => Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(invocation, replacement))),
                "CQRA014_UseAddCqrsGenerated"),
            context.Diagnostics);
    }

    private static InvocationExpressionSyntax? Rewrite(InvocationExpressionSyntax invocation, IMethodSymbol method)
    {
        // The extension form: the receiver already is the service collection, so only the name changes.
        if (method.MethodKind == MethodKind.ReducedExtension && invocation.Expression is MemberAccessExpressionSyntax access)
            return invocation.WithExpression(access.WithName(Renamed(access.Name)));

        // The static form passes the service collection, its only argument, which becomes the receiver.
        var arguments = invocation.ArgumentList.Arguments;
        if (arguments.Count != 1 || arguments[0].NameColon is not null) return null;

        var name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name,
            SimpleNameSyntax simple => simple,
            _ => null
        };
        if (name is null) return null;

        var receiver = arguments[0].Expression.WithoutTrivia();
        if (receiver is not (IdentifierNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax or ThisExpressionSyntax or ParenthesizedExpressionSyntax))
            receiver = SyntaxFactory.ParenthesizedExpression(receiver);

        return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, receiver, Renamed(name).WithoutTrivia()),
                invocation.ArgumentList.WithArguments(default))
            .WithTriviaFrom(invocation);
    }

    private static SimpleNameSyntax Renamed(SimpleNameSyntax name)
        => SyntaxFactory.IdentifierName(SyntaxFactory.Identifier("AddCqrsGenerated").WithTriviaFrom(name.Identifier));
}
