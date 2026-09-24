using CQRSharp.Analyzers;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.CodeAnalysis;

namespace CQRSharp.Tests.Analyzers;

/// <summary>
///     CQRA010 fires when a project declares CQRSharp handlers but the source generator is not running in it (detected
///     by the absence of the generator-emitted <c>[assembly: CqrsGeneratedModule]</c> marker and bootstrap).
/// </summary>
public sealed class GeneratorPresenceAnalyzerTests
{
    private const string HandlerSource = """
                                         using System.Threading;
                                         using System.Threading.Tasks;
                                         using CQRSharp;

                                         public sealed class MissingGenCommand : CommandBase;

                                         public sealed class MissingGenCommandHandler : ICommandHandler<MissingGenCommand>
                                         {
                                             public Task<CommandResult> Handle(MissingGenCommand command, CancellationToken ct)
                                                 => Task.FromResult(CommandResult.FromSuccess());
                                         }
                                         """;

    [Fact(DisplayName = "CQRA010: handlers but no generated-module marker (generator not running) is flagged")]
    public async Task Fires_when_the_generator_is_not_running()
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync(HandlerSource, new GeneratorPresenceAnalyzer());

        diagnostics.Should().ContainSingle(d => d.Id == "CQRA010" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact(DisplayName = "CQRA010: a handler project that references only CQRSharp.Abstractions is flagged")]
    public async Task Fires_in_an_abstractions_only_project()
    {
        var compilation = CompilationHarness.Compile([HandlerSource], references: ProbeReferences.AbstractionsOnly());

        var diagnostics = await CompilationHarness.AnalyzeAsync(compilation, new GeneratorPresenceAnalyzer());

        diagnostics.Should().ContainSingle(d => d.Id == "CQRA010");
    }

    [Fact(DisplayName = "CQRA010: not flagged when the generated-module marker is present (generator is running)")]
    public async Task Quiet_when_the_module_marker_is_present()
    {
        // Assembly attributes must precede type declarations, so the marker sits at the top.
        var diagnostics = await CompilationHarness.AnalyzeAsync("""
            using System.Threading;
            using System.Threading.Tasks;
            using CQRSharp;

            [assembly: global::CQRSharp.Core.SourceGeneration.CqrsGeneratedModule(typeof(MissingGenCommandHandler))]

            public sealed class MissingGenCommand : CommandBase;

            public sealed class MissingGenCommandHandler : ICommandHandler<MissingGenCommand>
            {
                public Task<CommandResult> Handle(MissingGenCommand command, CancellationToken ct)
                    => Task.FromResult(CommandResult.FromSuccess());
            }
            """, new GeneratorPresenceAnalyzer());

        diagnostics.Should().NotContain(d => d.Id == "CQRA010");
    }

    [Fact(DisplayName = "CQRA010: not flagged when the generator ran over the project")]
    public async Task Quiet_after_the_generator_ran()
    {
        var diagnostics = await CompilationHarness.AnalyzeWithGeneratorsAsync(HandlerSource, new GeneratorPresenceAnalyzer());

        diagnostics.Should().NotContain(d => d.Id == "CQRA010");
    }
}
