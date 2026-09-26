using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace CQRSharp.Analyzers;

/// <summary>
///     Code fix for CQRA020: marks the notification <c>[NotificationName("...")]</c> with a name in the lower-case, dotted
///     form the documentation and samples use (<c>OrderPlacedNotification</c> becomes <c>order.placed</c>). The name is
///     derived once, here, and is then a literal: renaming the type later does not change it, which is what keeps stored
///     messages readable. When that name is taken in the compilation, the namespace is put in front of it; when that is
///     taken too, no fix is offered.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UnnamedHandledNotificationCodeFixProvider))]
[Shared]
public sealed class UnnamedHandledNotificationCodeFixProvider : CodeFixProvider
{
    // Suffixes that say what a type is, not which notification it is.
    private static readonly string[] RoleSuffixes = { "Notification", "Event" };

    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(CqrsDiagnostics.UnnamedHandledNotification.Id);

    public override FixAllProvider? GetFixAllProvider() => null;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null || model is null) return;

        var declaration = root.FindNode(context.Span, getInnermostNodeForTie: true).FirstAncestorOrSelf<TypeDeclarationSyntax>();
        if (declaration is null || model.GetDeclaredSymbol(declaration, context.CancellationToken) is not INamedTypeSymbol type) return;

        var known = CqrsKnownSymbols.For(model.Compilation);
        if (known.NotificationNameAttribute is not { } attribute) return;

        var taken = TakenNames(model.Compilation.Assembly.GlobalNamespace, attribute);
        var name = Candidates(type).FirstOrDefault(candidate => candidate.Length > 0 && !taken.Contains(candidate));
        if (name is null) return;

        // The short name where it binds to the attribute there (a using CQRSharp, or the meta-package's global using).
        var attributeName = model.LookupNamespacesAndTypes(declaration.SpanStart, name: attribute.Name)
            .Any(symbol => SymbolEqualityComparer.Default.Equals(symbol, attribute))
            ? "NotificationName"
            : "global::CQRSharp.NotificationName";

        context.RegisterCodeFix(
            CodeAction.Create(
                $"Add [NotificationName(\"{name}\")]",
                _ =>
                {
                    var generator = SyntaxGenerator.GetGenerator(context.Document);
                    var nameAttribute = generator.Attribute(attributeName, generator.LiteralExpression(name));
                    return Task.FromResult(context.Document.WithSyntaxRoot(
                        root.ReplaceNode(declaration, generator.AddAttributes(declaration, nameAttribute))));
                },
                nameof(UnnamedHandledNotificationCodeFixProvider)),
            context.Diagnostics);
    }

    // The type's words (order.placed), then the same behind its namespace (shop.orders.order.placed).
    private static IEnumerable<string> Candidates(INamedTypeSymbol type)
    {
        var words = Words(WithoutRoleSuffix(type.Name));
        yield return words;

        if (!type.ContainingNamespace.IsGlobalNamespace)
            yield return type.ContainingNamespace.ToDisplayString().ToLowerInvariant() + "." + words;
    }

    private static string WithoutRoleSuffix(string name)
    {
        foreach (var suffix in RoleSuffixes)
            if (name.Length > suffix.Length && name.EndsWith(suffix, System.StringComparison.Ordinal))
                return name.Substring(0, name.Length - suffix.Length);
        return name;
    }

    // PascalCase to lower-case words joined by dots, an acronym kept as one word: HTTPRequestLogged -> http.request.logged.
    private static string Words(string name)
    {
        var words = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetterOrDigit(c))
            {
                if (words.Length > 0 && words[words.Length - 1] != '.') words.Append('.');
                continue;
            }

            var startsWord = i > 0 && char.IsUpper(c) && words.Length > 0 && words[words.Length - 1] != '.' &&
                             (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]) ||
                              (char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1])));
            if (startsWord) words.Append('.');
            words.Append(char.ToLowerInvariant(c));
        }

        return words.ToString().Trim('.');
    }

    // The [NotificationName]s the compilation already gives: a second type under one of them is CQRGEN002.
    private static HashSet<string> TakenNames(INamespaceSymbol ns, INamedTypeSymbol attribute)
    {
        var taken = new HashSet<string>(System.StringComparer.Ordinal);
        Collect(ns);
        return taken;

        void Collect(INamespaceOrTypeSymbol container)
        {
            foreach (var member in container.GetMembers())
                switch (member)
                {
                    case INamespaceSymbol nested:
                        Collect(nested);
                        break;
                    case INamedTypeSymbol type:
                        foreach (var data in type.GetAttributes())
                            if (SymbolEqualityComparer.Default.Equals(data.AttributeClass, attribute) &&
                                data.ConstructorArguments.FirstOrDefault().Value is string value)
                                taken.Add(value);
                        Collect(type);
                        break;
                }
        }
    }
}
