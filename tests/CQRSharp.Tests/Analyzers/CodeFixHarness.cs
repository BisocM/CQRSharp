using System.Collections.Immutable;
using CQRSharp.Tests.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace CQRSharp.Tests.Analyzers;

/// <summary>
///     Runs a code fix the way the IDE does: the analyzer reports on a snippet in an in-memory workspace, the provider is
///     asked for its fixes, and the one offered is applied. The result is compiled again, so a fix that produces code that
///     does not compile fails its test instead of passing on the text alone.
/// </summary>
internal static class CodeFixHarness
{
    private const string SnippetName = "Snippet.cs";

    /// <summary>
    ///     Applies <paramref name="provider" />'s fix for the one <paramref name="diagnosticId" /> diagnostic
    ///     <paramref name="analyzer" /> reports in <paramref name="source" />. With <paramref name="withGeneratedCode" />,
    ///     the CQRSharp generators' output joins the project, as in a build. The project is a library unless
    ///     <paramref name="outputKind" /> says otherwise, and references <see cref="ProbeReferences.Create" /> unless
    ///     <paramref name="references" /> are given.
    /// </summary>
    public static async Task<CodeFixResult> ApplyAsync(
        string source,
        DiagnosticAnalyzer analyzer,
        CodeFixProvider provider,
        string diagnosticId,
        bool withGeneratedCode = false,
        OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary,
        MetadataReference[]? references = null)
    {
        references ??= ProbeReferences.Create();
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId("CodeFixUnderTest");
        var documentId = DocumentId.CreateNewId(projectId, SnippetName);

        var solution = workspace.CurrentSolution
            .AddProject(projectId, "CodeFixUnderTest", "CodeFixUnderTest", LanguageNames.CSharp)
            .WithProjectCompilationOptions(projectId,
                new CSharpCompilationOptions(outputKind, nullableContextOptions: NullableContextOptions.Enable))
            .AddMetadataReferences(projectId, references)
            .AddDocument(documentId, SnippetName, SourceText.From(source));

        if (withGeneratedCode)
            foreach (var (hintName, text) in CompilationHarness.RunGenerators([source], assemblyName: "CodeFixUnderTest", references: references, outputKind: outputKind).Sources)
                solution = solution.AddDocument(DocumentId.CreateNewId(projectId, hintName), hintName, SourceText.From(text));

        var document = solution.GetDocument(documentId)!;
        var compilation = (await document.Project.GetCompilationAsync())!;
        var inputErrors = Errors(compilation);
        if (inputErrors.Length > 0)
            throw new InvalidOperationException("The test input does not compile: " + string.Join(" | ", inputErrors));

        var diagnostic = (await CompilationHarness.AnalyzeAsync(compilation, analyzer))
            .Single(d => d.Id == diagnosticId && d.Location.SourceTree?.FilePath == SnippetName);

        var actions = new List<CodeAction>();
        await provider.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None));
        if (actions.Count == 0) return new CodeFixResult(actions, null, [], []);

        var operations = await actions.Single().GetOperationsAsync(CancellationToken.None);
        var fixedDocument = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution.GetDocument(documentId)!;
        var fixedCompilation = (await fixedDocument.Project.GetCompilationAsync())!;

        return new CodeFixResult(
            actions,
            (await fixedDocument.GetTextAsync()).ToString(),
            Errors(fixedCompilation),
            (await CompilationHarness.AnalyzeAsync(fixedCompilation, analyzer)).Where(d => d.Id == diagnosticId).ToImmutableArray());
    }

    private static string[] Errors(Compilation compilation)
        => compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Id} {d.Location.SourceTree?.FilePath}: {d.GetMessage()}")
            .ToArray();
}

/// <summary>The fixes a provider offered, and the snippet and its compilation after the one offered was applied.</summary>
/// <param name="Actions">The fixes offered; empty when the provider declined.</param>
/// <param name="FixedSource">The snippet after the fix, or null when none was offered.</param>
/// <param name="CompileErrors">The errors of the fixed project.</param>
/// <param name="RemainingDiagnostics">The fixed diagnostic's reports that are left after the fix.</param>
internal sealed record CodeFixResult(
    IReadOnlyList<CodeAction> Actions,
    string? FixedSource,
    string[] CompileErrors,
    ImmutableArray<Diagnostic> RemainingDiagnostics);
