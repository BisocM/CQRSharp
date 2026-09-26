using CQRSharp.Analyzers;
using CQRSharp.Tests.Shared;
using FluentAssertions;

namespace CQRSharp.Tests.Analyzers;

/// <summary>CQRA011: a request declares a custom context but no factory for it is discoverable.</summary>
public sealed class MissingContextFactoryAnalyzerTests
{
    private const string Usings = """
                                  using System;
                                  using CQRSharp;

                                  """;

    private const string CustomContextRequest = Usings + """
                                                         public sealed class MyContext : IRequestContext
                                                         {
                                                             public DateTime CreatedAt => default;
                                                         }

                                                         public sealed class FancyCommand : CommandBase<MyContext>;

                                                         """;

    [Fact(DisplayName = "CQRA011: a custom-context request without a factory is flagged")]
    public async Task Custom_context_without_a_factory_is_flagged()
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync(CustomContextRequest, new MissingContextFactoryAnalyzer());

        diagnostics.Should().ContainSingle(d => d.Id == "CQRA011");
    }

    [Fact(DisplayName = "CQRA011: a custom-context request with a factory present is not flagged")]
    public async Task Custom_context_with_a_factory_is_not_flagged()
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync(CustomContextRequest + """
            public sealed class MyContextFactory : IRequestContextFactory<MyContext>
            {
                public System.Threading.Tasks.ValueTask<MyContext> CreateContextAsync(IRequest request, System.Threading.CancellationToken cancellationToken) => new(new MyContext());
            }
            """, new MissingContextFactoryAnalyzer());

        diagnostics.Should().NotContain(d => d.Id == "CQRA011");
    }

    [Fact(DisplayName = "CQRA011: a factory advertised by a generator marker in this compilation counts")]
    public async Task Own_factory_marker_counts()
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync(
            CustomContextRequest.Replace(Usings, Usings + "[assembly: CQRSharp.Core.SourceGeneration.CqrsRegisteredContextFactory(typeof(MyContext))]\n"),
            new MissingContextFactoryAnalyzer());

        diagnostics.Should().NotContain(d => d.Id == "CQRA011");
    }

    [Fact(DisplayName = "CQRA011: the default context (CommandBase) is not flagged")]
    public async Task Default_context_is_not_flagged()
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync("""
            using CQRSharp;

            public sealed class PlainCommand : CommandBase;
            """, new MissingContextFactoryAnalyzer());

        diagnostics.Should().NotContain(d => d.Id == "CQRA011");
    }

    [Fact(DisplayName = "CQRA011 does not flag a request that is generic over its context")]
    public async Task Generic_context_is_not_flagged()
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync("""
            using CQRSharp;

            public sealed class Ping<TContext> : CommandBase<TContext> where TContext : RequestContextBase;
            """, new MissingContextFactoryAnalyzer());

        diagnostics.Should().NotContain(d => d.Id == "CQRA011");
    }

    [Fact(DisplayName = "CQRA011 stays quiet in a contracts project that references only CQRSharp.Abstractions")]
    public async Task Abstractions_only_project_is_not_flagged()
    {
        var compilation = CompilationHarness.Compile([CustomContextRequest], references: ProbeReferences.AbstractionsOnly());

        var diagnostics = await CompilationHarness.AnalyzeAsync(compilation, new MissingContextFactoryAnalyzer());

        diagnostics.Should().NotContain(d => d.Id == "CQRA011");
    }
}
