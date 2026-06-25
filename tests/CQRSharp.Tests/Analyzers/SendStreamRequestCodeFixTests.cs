using System.Collections.Immutable;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Analyzers;
using CQRSharp.Core.Mediation;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace CQRSharp.Tests.Analyzers;

/// <summary>
///     Exercises <see cref="SendStreamRequestCodeFixProvider" /> (the CQRA004 Send->Stream code fix). A Document is
///     built in an in-memory workspace that references the real CQRSharp assemblies, the analyzer is run to obtain the
///     genuine CQRA004 diagnostic, the provider's <c>RegisterCodeFixesAsync</c> is invoked against that diagnostic, the
///     offered <see cref="CodeAction" /> is applied, and the resulting source is asserted to have replaced
///     <c>Send</c> with <c>Stream</c>.
/// </summary>
public class SendStreamRequestCodeFixTests
{
    private const string StreamSendSource = """
                                            using System.Threading.Tasks;
                                            using CQRSharp.Abstractions.Interfaces.Context;
                                            using CQRSharp.Abstractions.Interfaces.Markers.Stream;
                                            using CQRSharp.Abstractions.Models.Requests;
                                            using CQRSharp.Core.Mediation;

                                            public sealed class MyStream : IStreamRequest<int>
                                            {
                                                public IRequestContext? Context { get; set; }
                                                public RequestMetadata? Metadata { get; set; }
                                            }

                                            public class Consumer
                                            {
                                                public async Task Run(ICqrsDispatcher dispatcher) => await dispatcher.Send(new MyStream());
                                            }
                                            """;

    /// <summary>
    ///     Builds an in-memory <see cref="Document" /> referencing the real CQRSharp assemblies (plus the trusted
    ///     platform assemblies, mirroring <see cref="CqrsAnalyzerTests" />) so the snippet compiles cleanly.
    /// </summary>
    private static Document CreateDocument(AdhocWorkspace workspace, string source)
    {
        var paths = new HashSet<string>(
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrEmpty(p)),
            StringComparer.OrdinalIgnoreCase)
        {
            typeof(IRequest).Assembly.Location,
            typeof(ICqrsDispatcher).Assembly.Location
        };

        var references = paths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray();

        var projectId = ProjectId.CreateNewId("CodeFixUnderTest");
        var documentId = DocumentId.CreateNewId(projectId, "Snippet.cs");

        var solution = workspace.CurrentSolution
            .AddProject(projectId, "CodeFixUnderTest", "CodeFixUnderTest", LanguageNames.CSharp)
            .WithProjectCompilationOptions(projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(projectId, references)
            .AddDocument(documentId, "Snippet.cs", SourceText.From(source));

        workspace.TryApplyChanges(solution).Should().BeTrue("the in-memory solution should apply cleanly");
        return workspace.CurrentSolution.GetDocument(documentId)!;
    }

    /// <summary>
    ///     Runs <see cref="SendStreamRequestAnalyzer" /> over the document's compilation and returns the analyzer
    ///     diagnostics (after asserting the snippet itself has no compile errors).
    /// </summary>
    private static async Task<ImmutableArray<Diagnostic>> GetAnalyzerDiagnosticsAsync(Document document)
    {
        var compilation = (await document.Project.GetCompilationAsync())!;

        var compileErrors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (compileErrors.Length > 0)
            throw new InvalidOperationException(
                "Test snippet failed to compile: " + string.Join(" | ", compileErrors.Select(e => e.ToString())));

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new SendStreamRequestAnalyzer()));
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    [Fact(DisplayName = "CQRA004 code fix: provider registers a 'Use Stream(...)' fix for the diagnostic")]
    public async Task RegistersUseStreamFix()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, StreamSendSource);

        var diagnostic = (await GetAnalyzerDiagnosticsAsync(document))
            .Should().ContainSingle(d => d.Id == "CQRA004").Which;

        var provider = new SendStreamRequestCodeFixProvider();
        provider.FixableDiagnosticIds.Should().Contain("CQRA004");

        var actions = new List<CodeAction>();
        var context = new CodeFixContext(
            document,
            diagnostic,
            (action, _) => actions.Add(action),
            CancellationToken.None);

        await provider.RegisterCodeFixesAsync(context);

        actions.Should().ContainSingle()
            .Which.Title.Should().Be("Use Stream(...)");
    }

    [Fact(DisplayName = "CQRA004 code fix: applying the action replaces Send with Stream")]
    public async Task ApplyingFixReplacesSendWithStream()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, StreamSendSource);

        var diagnostic = (await GetAnalyzerDiagnosticsAsync(document))
            .Should().ContainSingle(d => d.Id == "CQRA004").Which;

        var provider = new SendStreamRequestCodeFixProvider();

        var actions = new List<CodeAction>();
        var context = new CodeFixContext(
            document,
            diagnostic,
            (action, _) => actions.Add(action),
            CancellationToken.None);

        await provider.RegisterCodeFixesAsync(context);

        var action = actions.Should().ContainSingle().Subject;

        // Apply the CodeAction: materialise its operations and pull the changed document back out of the solution.
        var operations = await action.GetOperationsAsync(CancellationToken.None);
        var applyChanges = operations.OfType<ApplyChangesOperation>().Should().ContainSingle().Subject;

        var changedDocument = applyChanges.ChangedSolution.GetDocument(document.Id)!;
        var changedText = (await changedDocument.GetTextAsync()).ToString();

        changedText.Should().Contain("dispatcher.Stream(new MyStream())");
        changedText.Should().NotContain(".Send(");
    }

    [Fact(DisplayName = "CQRA004 code fix: FixableDiagnosticIds contains only CQRA004")]
    public void FixableDiagnosticIds_AreScopedToCqra004()
    {
        var provider = new SendStreamRequestCodeFixProvider();
        provider.FixableDiagnosticIds.Should().Equal("CQRA004");
    }
}