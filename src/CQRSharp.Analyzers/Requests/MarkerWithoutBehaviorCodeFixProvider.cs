using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CQRSharp.Analyzers;

/// <summary>
///     Code fix for CQRA018 / CQRA019: adds the missing verb to the application's <c>AddCqrsGenerated</c> configuration,
///     <c>UseIdempotency()</c> (the in-memory store, which a durable store chosen later replaces) or
///     <c>UseResilience(o =&gt; o.MaxRetries = 3)</c> (the default retry count, spelled out to be tuned). A fluent chain gets
///     one more link, a statement lambda one more statement, and the parameterless call a configuration of its own.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MarkerWithoutBehaviorCodeFixProvider))]
[Shared]
public sealed class MarkerWithoutBehaviorCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(CqrsDiagnostics.IdempotentRequestWithoutIdempotency.Id, CqrsDiagnostics.RetryableRequestWithoutResilience.Id);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null || model is null) return;

        foreach (var diagnostic in context.Diagnostics)
        {
            if (!diagnostic.Properties.TryGetValue(MarkerWithoutBehaviorAnalyzer.VerbProperty, out var verb) || verb is null) continue;

            var registration = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
                .FirstAncestorOrSelf<InvocationExpressionSyntax>();
            if (registration is null || WithVerb(registration, verb, model) is not { } rewritten) continue;

            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Call {verb}(...) on the CQRSharp builder",
                    _ => Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(registration, rewritten))),
                    "CQRA018_" + verb),
                diagnostic);
        }
    }

    private static InvocationExpressionSyntax? WithVerb(InvocationExpressionSyntax registration, string verb, SemanticModel model)
    {
        var position = registration.SpanStart;
        var lambda = registration.ArgumentList.Arguments
            .Select(a => a.Expression)
            .OfType<LambdaExpressionSyntax>()
            .FirstOrDefault();

        if (lambda is null)
        {
            // The parameterless AddCqrsGenerated() (or its static form): the builder overload with a configuration of its own.
            var parameter = FreeName(model, position, "b", "builder", "cqrs");
            if (parameter is null) return null;

            var configure = SyntaxFactory.SimpleLambdaExpression(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier(parameter)),
                Link(SyntaxFactory.IdentifierName(parameter), verb, model, position));
            return registration.WithArgumentList(registration.ArgumentList.AddArguments(SyntaxFactory.Argument(configure)));
        }

        var parameterName = lambda switch
        {
            SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.ValueText,
            ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 1 } parenthesized => parenthesized.ParameterList.Parameters[0].Identifier.ValueText,
            _ => null
        };
        if (parameterName is null) return null;

        // Names are looked up inside the body, where the builder parameter is in scope too.
        LambdaExpressionSyntax updated;
        if (lambda.Block is { } block)
        {
            var statement = SyntaxFactory.ExpressionStatement(Link(SyntaxFactory.IdentifierName(parameterName), verb, model, block.OpenBraceToken.Span.End))
                .WithAdditionalAnnotations(Formatter.Annotation);
            updated = lambda.WithBlock(block.AddStatements(statement));
        }
        else if (lambda.ExpressionBody is { } body)
        {
            updated = lambda.WithExpressionBody(Link(body, verb, model, body.SpanStart));
        }
        else
        {
            return null;
        }

        return registration.ReplaceNode(lambda, updated);
    }

    // chain.Verb(...), laid out like the chain: on a line of its own when the chain's last link is.
    private static InvocationExpressionSyntax Link(ExpressionSyntax chain, string verb, SemanticModel model, int position)
    {
        var trailing = chain.GetTrailingTrivia();
        var receiver = chain.WithoutTrailingTrivia();
        var dot = SyntaxFactory.Token(SyntaxKind.DotToken);
        if (chain is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax lastLink } &&
            lastLink.OperatorToken.GetPreviousToken().TrailingTrivia.Any(SyntaxKind.EndOfLineTrivia))
        {
            receiver = receiver.WithTrailingTrivia(lastLink.OperatorToken.GetPreviousToken().TrailingTrivia);
            dot = dot.WithLeadingTrivia(lastLink.OperatorToken.LeadingTrivia);
        }

        return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, receiver, dot, SyntaxFactory.IdentifierName(verb)),
                Arguments(verb, model, position))
            .WithTrailingTrivia(trailing);
    }

    private static ArgumentListSyntax Arguments(string verb, SemanticModel model, int position)
    {
        if (verb != "UseResilience") return SyntaxFactory.ArgumentList();

        // The default retry count, spelled out so the call shows what to tune.
        var options = FreeName(model, position, "o", "options", "resilience") ?? "resilienceOptions";
        var configure = SyntaxFactory.SimpleLambdaExpression(
            SyntaxFactory.Parameter(SyntaxFactory.Identifier(options)),
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName(options), SyntaxFactory.IdentifierName("MaxRetries")),
                SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(3))));
        return SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(configure)));
    }

    // A lambda parameter name nothing in scope uses, so the new lambda neither shadows nor clashes.
    private static string? FreeName(SemanticModel model, int position, params string[] candidates)
        => candidates.FirstOrDefault(name => model.LookupSymbols(position, name: name).IsEmpty);
}
