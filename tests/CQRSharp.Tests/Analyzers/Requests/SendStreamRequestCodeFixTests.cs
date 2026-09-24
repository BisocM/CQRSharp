using CQRSharp.Analyzers;
using FluentAssertions;

namespace CQRSharp.Tests.Analyzers;

/// <summary>
///     The CQRA004 code fix. <c>Send</c> returns a task of the stream and <c>Stream</c> the stream itself, so the fix
///     rewrites the awaited call as a whole; each case compiles the fixed project.
/// </summary>
public sealed class SendStreamRequestCodeFixTests
{
    private const string Usings = """
                                  using System.Collections.Generic;
                                  using System.Threading.Tasks;
                                  using CQRSharp;

                                  public sealed class Names : StreamRequestBase<string>;

                                  """;

    [Fact(DisplayName = "CQRA004 code fix: the provider fixes CQRA004 only")]
    public void Fixes_cqra004_only()
        => new SendStreamRequestCodeFixProvider().FixableDiagnosticIds.Should().Equal("CQRA004");

    [Theory(DisplayName = "CQRA004 code fix: an awaited Send becomes a Stream call that compiles")]
    [InlineData(
        "public static async Task Run(ICqrsDispatcher d) => await d.Send(new Names());",
        "public static async Task Run(ICqrsDispatcher d) => d.Stream(new Names());")]
    [InlineData(
        "public static async Task Run(ICqrsDispatcher d) { var names = await d.Send(new Names()); await foreach (var name in names) { } }",
        "var names = d.Stream(new Names());")]
    [InlineData(
        "public static async Task Run(ICqrsDispatcher d) { var names = await d.Send(new Names()).ConfigureAwait(false); await foreach (var name in names) { } }",
        "var names = d.Stream(new Names());")]
    [InlineData(
        "public static async Task Run(ICqrsDispatcher d) { await foreach (var name in (await d.Send<IAsyncEnumerable<string>>(new Names()))) { } }",
        "await foreach (var name in (d.Stream(new Names())))")]
    [InlineData(
        "public static async Task<object?> Run(ICqrsDispatcher d) => await d.Send((object)new Names());",
        "=> d.Stream((object)new Names());")]
    public async Task Awaited_send_becomes_stream(string member, string expected)
    {
        var result = await CodeFixHarness.ApplyAsync(
            Usings + "public static class Consumer { " + member + " }",
            new SendStreamRequestAnalyzer(), new SendStreamRequestCodeFixProvider(), "CQRA004");

        result.Actions.Should().ContainSingle().Which.Title.Should().Be("Use Stream(...)");
        result.FixedSource.Should().Contain(expected).And.NotContain(".Send");
        result.CompileErrors.Should().BeEmpty();
        result.RemainingDiagnostics.Should().BeEmpty();
    }

    [Fact(DisplayName = "CQRA004 code fix: no fix is offered when the Send task is used as a value")]
    public async Task Task_used_as_a_value_is_not_rewritten()
    {
        var result = await CodeFixHarness.ApplyAsync(
            Usings + "public static class Consumer { public static Task<IAsyncEnumerable<string>> Run(ICqrsDispatcher d) => d.Send(new Names()); }",
            new SendStreamRequestAnalyzer(), new SendStreamRequestCodeFixProvider(), "CQRA004");

        result.Actions.Should().BeEmpty();
    }
}
