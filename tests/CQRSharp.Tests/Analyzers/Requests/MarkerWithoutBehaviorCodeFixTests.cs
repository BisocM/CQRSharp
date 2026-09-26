using CQRSharp.Analyzers;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.CodeAnalysis;

namespace CQRSharp.Tests.Analyzers;

/// <summary>
///     The CQRA018 / CQRA019 code fix: the missing verb joins the application's configuration, laid out like it. Each case
///     runs the generators, so the fixed application compiles against the real generated entry points and the diagnostic
///     is gone.
/// </summary>
public sealed class MarkerWithoutBehaviorCodeFixTests
{
    [Theory(DisplayName = "CQRA018 code fix: UseIdempotency() joins the configuration, and the fixed application compiles")]
    [InlineData("services.AddCqrsGenerated()", "services.AddCqrsGenerated(b => b.UseIdempotency())")]
    [InlineData("services.AddCqrsGenerated(b => b.UseLogging())", "services.AddCqrsGenerated(b => b.UseLogging().UseIdempotency())")]
    [InlineData("services.AddCqrsGenerated(builder => builder.UseLogging().ValidateOnStart())", "services.AddCqrsGenerated(builder => builder.UseLogging().ValidateOnStart().UseIdempotency())")]
    [InlineData("services.AddCqrsGenerated(b => { })", "services.AddCqrsGenerated(b => { b.UseIdempotency(); })")]
    public async Task Fix_adds_UseIdempotency(string registration, string expected)
    {
        var result = await ApplyAsync(registration, "CQRA018");

        result.Actions.Should().ContainSingle().Which.Title.Should().Be("Call UseIdempotency(...) on the CQRSharp builder");
        Normalized(result.FixedSource!).Should().Contain(Normalized(expected));
        result.CompileErrors.Should().BeEmpty();
        result.RemainingDiagnostics.Should().BeEmpty();
    }

    [Fact(DisplayName = "CQRA019 code fix: UseResilience with the default retry count joins the configuration")]
    public async Task Fix_adds_UseResilience()
    {
        var result = await ApplyAsync("services.AddCqrsGenerated(b => b.UseLogging())", "CQRA019");

        result.FixedSource.Should().Contain("services.AddCqrsGenerated(b => b.UseLogging().UseResilience(o => o.MaxRetries = 3))");
        result.CompileErrors.Should().BeEmpty();
        result.RemainingDiagnostics.Should().BeEmpty();
    }

    [Fact(DisplayName = "CQRA018 code fix: a chain laid out one link per line gets the new link on a line of its own")]
    public async Task Fix_follows_a_multi_line_chain()
    {
        const string registration = """
                                    services.AddCqrsGenerated(b => b
                                                .UseLogging()
                                                .ValidateOnStart())
                                    """;

        var result = await ApplyAsync(registration, "CQRA018");

        result.FixedSource.Should().Contain("""
                                                        .ValidateOnStart()
                                                        .UseIdempotency())
                                            """);
        result.CompileErrors.Should().BeEmpty();
    }

    [Fact(DisplayName = "CQRA018 code fix: the new lambda's parameter does not clash with a name in scope")]
    public async Task Fix_picks_a_free_parameter_name()
    {
        var result = await ApplyAsync("var b = 1; _ = b; services.AddCqrsGenerated()", "CQRA018");

        result.FixedSource.Should().Contain("services.AddCqrsGenerated(builder => builder.UseIdempotency())");
        result.CompileErrors.Should().BeEmpty();
    }

    // The request of the other marker is left out, so each case has exactly the diagnostic it fixes.
    private static Task<CodeFixResult> ApplyAsync(string registration, string id)
    {
        var requests = id == "CQRA018"
            ? MarkerWithoutBehaviorAnalyzerTests.Requests.Replace("public sealed class Charge : CommandBase, IRetryableRequest;", "public sealed class Charge : CommandBase;")
            : MarkerWithoutBehaviorAnalyzerTests.Requests.Replace("public sealed class PlaceOrder : CommandBase, IIdempotentRequest", "public sealed class PlaceOrder : CommandBase");

        return CodeFixHarness.ApplyAsync(
            requests + MarkerWithoutBehaviorAnalyzerTests.Program(registration),
            new MarkerWithoutBehaviorAnalyzer(),
            new MarkerWithoutBehaviorCodeFixProvider(),
            id,
            withGeneratedCode: true,
            outputKind: OutputKind.ConsoleApplication,
            references: ProbeReferences.SingleProjectApplication());
    }

    private static string Normalized(string text) => string.Join(" ", text.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
}
