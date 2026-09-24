using CQRSharp.Analyzers;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.CodeAnalysis;

namespace CQRSharp.Tests.Analyzers;

/// <summary>CQRA014: the low-level <c>AddCqrs()</c> called directly.</summary>
public sealed class DirectCoreRegistrationAnalyzerTests
{
    private const string Usings = """
                                  using CQRSharp;
                                  using CQRSharp.Pipelines;
                                  using Microsoft.Extensions.DependencyInjection;

                                  """;

    [Fact(DisplayName = "CQRA014: a direct AddCqrs() call is flagged as an error")]
    public async Task Direct_AddCqrs_is_flagged()
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync(Usings + """
            public class Startup
            {
                public void Configure(IServiceCollection services) => services.AddCqrs();
            }
            """, new DirectCoreRegistrationAnalyzer());

        diagnostics.Should().ContainSingle(d => d.Id == "CQRA014" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact(DisplayName = "CQRA014: an unrelated registration call and the generated AddCqrsGenerated are not flagged")]
    public async Task Other_registrations_are_not_flagged()
    {
        var diagnostics = await CompilationHarness.AnalyzeWithGeneratorsAsync(Usings + """
            public class Startup
            {
                public void Configure(IServiceCollection services) => services.AddSingleton<string>("x").AddCqrsGenerated(b => b.UseLogging());
            }
            """, new DirectCoreRegistrationAnalyzer());

        diagnostics.Should().NotContain(d => d.Id == "CQRA014");
    }
}
