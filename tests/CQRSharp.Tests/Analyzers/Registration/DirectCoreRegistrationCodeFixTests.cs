using CQRSharp.Analyzers;
using FluentAssertions;

namespace CQRSharp.Tests.Analyzers;

/// <summary>
///     The CQRA014 code fix: <c>AddCqrs()</c> becomes the generated <c>AddCqrsGenerated()</c>. Each case runs the
///     generators, as a build does, so the fixed project compiles against the real generated entry points.
/// </summary>
public sealed class DirectCoreRegistrationCodeFixTests
{
    private const string Usings = """
                                  using CQRSharp;
                                  using CQRSharp.Pipelines;
                                  using Microsoft.Extensions.DependencyInjection;

                                  """;

    [Theory(DisplayName = "CQRA014 code fix: the call becomes AddCqrsGenerated on the service collection, and compiles")]
    [InlineData("services.AddCqrs()", "services.AddCqrsGenerated()")]
    [InlineData("CQRSharp.DependencyInjectionExtensions.AddCqrs(services)", "services.AddCqrsGenerated()")]
    [InlineData("DependencyInjectionExtensions.AddCqrs(services)", "services.AddCqrsGenerated()")]
    public async Task Fix_calls_AddCqrsGenerated(string call, string expected)
    {
        var result = await CodeFixHarness.ApplyAsync(
            Usings + "public class Startup { public void Configure(IServiceCollection services) => " + call + "; }",
            new DirectCoreRegistrationAnalyzer(), new DirectCoreRegistrationCodeFixProvider(), "CQRA014", withGeneratedCode: true);

        result.Actions.Should().ContainSingle().Which.Title.Should().Be("Use AddCqrsGenerated()");
        result.FixedSource.Should().Contain("=> " + expected + ";");
        result.CompileErrors.Should().BeEmpty();
        result.RemainingDiagnostics.Should().BeEmpty();
    }

    [Fact(DisplayName = "CQRA014 code fix: withheld where no generated AddCqrsGenerated is in view")]
    public async Task Fix_is_withheld_without_the_generated_bootstrap()
    {
        var result = await CodeFixHarness.ApplyAsync(
            Usings + "public class Startup { public void Configure(IServiceCollection services) => services.AddCqrs(); }",
            new DirectCoreRegistrationAnalyzer(), new DirectCoreRegistrationCodeFixProvider(), "CQRA014", withGeneratedCode: false);

        result.Actions.Should().BeEmpty();
    }
}
