using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Analyzers;
using CQRSharp.Core.Mediation;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CQRSharp.Tests.Analyzers;

/// <summary>
///     CQRA010 fires when a project declares CQRSharp handlers but the source generator is not running in it (detected
///     by the absence of the generator-emitted <c>[assembly: CqrsGeneratedModule]</c> marker). The two states are
///     simulated by including / omitting that marker in the snippet.
/// </summary>
public sealed class GeneratorPresenceAnalyzerTests
{
    private const string HandlerSource = """
                                         using System.Threading;
                                         using System.Threading.Tasks;
                                         using CQRSharp.Abstractions.Interfaces.Handlers;
                                         using CQRSharp.Abstractions.Interfaces.Markers.Command;
                                         using CQRSharp.Abstractions.Models.Commands;

                                         public sealed class MissingGenCommand : CommandBase;

                                         public sealed class MissingGenCommandHandler : ICommandHandler<MissingGenCommand>
                                         {
                                             public Task<CommandResult> Handle(MissingGenCommand command, CancellationToken ct)
                                                 => Task.FromResult(CommandResult.FromSuccess());
                                         }
                                         """;

    [Fact(DisplayName = "CQRA010: handlers but no generated-module marker (generator not running) is flagged")]
    public async Task Cqra010_FiresWhenGeneratorNotRunning()
    {
        var diagnostics = await AnalyzeAsync(HandlerSource, new GeneratorPresenceAnalyzer());
        diagnostics.Should().Contain(d => d.Id == "CQRA010" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact(DisplayName = "CQRA010: not flagged when the generated-module marker is present (generator is running)")]
    public async Task Cqra010_QuietWhenGeneratorRan()
    {
        // The marker the generator emits when it runs in an assembly with handlers; its presence means they're wired.
        // (Assembly attributes must precede type declarations, so it lives at the top here.)
        const string withMarker = """
                                  using System.Threading;
                                  using System.Threading.Tasks;
                                  using CQRSharp.Abstractions.Interfaces.Handlers;
                                  using CQRSharp.Abstractions.Interfaces.Markers.Command;
                                  using CQRSharp.Abstractions.Models.Commands;

                                  [assembly: global::CQRSharp.Abstractions.Attributes.SourceGeneration.CqrsGeneratedModule(typeof(MissingGenCommandHandler))]

                                  public sealed class MissingGenCommand : CommandBase;

                                  public sealed class MissingGenCommandHandler : ICommandHandler<MissingGenCommand>
                                  {
                                      public Task<CommandResult> Handle(MissingGenCommand command, CancellationToken ct)
                                          => Task.FromResult(CommandResult.FromSuccess());
                                  }
                                  """;

        var diagnostics = await AnalyzeAsync(withMarker, new GeneratorPresenceAnalyzer());
        diagnostics.Should().NotContain(d => d.Id == "CQRA010");
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, params DiagnosticAnalyzer[] analyzers)
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

        var compilation = CSharpCompilation.Create(
            "Cqra010UnderTest",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var compileErrors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (compileErrors.Length > 0)
            throw new InvalidOperationException("Test snippet failed to compile: " + string.Join(" | ", compileErrors.Select(e => e.ToString())));

        return await compilation.WithAnalyzers(analyzers.ToImmutableArray()).GetAnalyzerDiagnosticsAsync();
    }
}
