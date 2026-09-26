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
///     Code fix for CQRA004: dispatches the stream request with <c>Stream(...)</c> instead of <c>Send(...)</c>.
/// </summary>
/// <remarks>
///     <c>Send</c> returns a task of the stream, <c>Stream</c> returns the stream itself, so the fix replaces the whole
///     <c>await d.Send(r)</c> (with or without <c>.ConfigureAwait(...)</c>) by <c>d.Stream(r)</c>: the expression keeps
///     its type, <c>IAsyncEnumerable&lt;T&gt;</c>. When the task is used any other way (stored, returned, passed on)
///     there is no equivalent rewrite, and no fix is offered. It is also withheld when the rewritten call would not bind
///     to <c>Stream</c>.
/// </remarks>
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

        var name = root.FindNode(context.Span, getInnermostNodeForTie: true).FirstAncestorOrSelf<SimpleNameSyntax>();
        if (name is null || name.Identifier.ValueText != "Send") return;

        var invocation = name.Parent switch
        {
            MemberAccessExpressionSyntax access when access.Name == name => access.Parent as InvocationExpressionSyntax,
            InvocationExpressionSyntax call when call.Expression == name => call,
            _ => null
        };
        if (invocation is null) return;

        var awaitExpression = AwaitOf(invocation);
        if (awaitExpression is null) return;

        // Stream<TItem> infers its item type from the request; Send's explicit type argument (the whole stream type)
        // would not fit it.
        var streamName = SyntaxFactory.IdentifierName(SyntaxFactory.Identifier("Stream").WithTriviaFrom(name.Identifier));
        var streamCall = invocation.ReplaceNode(name, streamName)
            .WithLeadingTrivia(awaitExpression.GetLeadingTrivia())
            .WithTrailingTrivia(awaitExpression.GetTrailingTrivia());

        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        var bound = model?.GetSpeculativeSymbolInfo(awaitExpression.SpanStart, streamCall, SpeculativeBindingOption.BindAsExpression).Symbol;
        if (bound is not IMethodSymbol { Name: "Stream" }) return;

        context.RegisterCodeFix(
            CodeAction.Create(
                "Use Stream(...)",
                _ => Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(awaitExpression, streamCall))),
                "CQRA004_UseStream"),
            context.Diagnostics);
    }

    // The await that consumes the Send task, directly or through ConfigureAwait(...) (and any parentheses), or null
    // when the task is used some other way.
    private static AwaitExpressionSyntax? AwaitOf(InvocationExpressionSyntax invocation)
    {
        ExpressionSyntax awaited = invocation;
        while (true)
        {
            switch (awaited.Parent)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    awaited = parenthesized;
                    continue;
                case MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ConfigureAwait" } access when access.Expression == awaited &&
                                                                                                     access.Parent is InvocationExpressionSyntax configured:
                    awaited = configured;
                    continue;
                case AwaitExpressionSyntax awaitExpression:
                    return awaitExpression;
                default:
                    return null;
            }
        }
    }
}
