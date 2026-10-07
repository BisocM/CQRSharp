using CQRSharp.Analyzers;
using FluentAssertions;

namespace CQRSharp.Tests.Analyzers;

/// <summary>
///     Every code fix writes the line endings of the file it fixes. The formatter's default newline is the platform's
///     (CRLF on Windows, LF elsewhere), so a fix that leaves a new line to the formatter writes CRLF into an LF file on
///     Windows and LF into a CRLF file elsewhere. Each fix runs on a file of each kind, so either platform catches it.
/// </summary>
public sealed class CodeFixLineEndingTests
{
    private static readonly Dictionary<string, Func<string, Task<CodeFixResult>>> Fixes = new()
    {
        ["CQRA004: Send becomes Stream"] = eol => CodeFixHarness.ApplyAsync(
            WithLineEndings(SendStreamRequestCodeFixTests.Usings +
                            "public static class Consumer\n" +
                            "{\n" +
                            "    public static async Task Run(ICqrsDispatcher d)\n" +
                            "    {\n" +
                            "        var names = await d.Send(new Names());\n" +
                            "        await foreach (var name in names) { }\n" +
                            "    }\n" +
                            "}\n", eol),
            new SendStreamRequestAnalyzer(), new SendStreamRequestCodeFixProvider(), "CQRA004"),

        ["CQRA008: the closed exemption becomes the open one"] = eol => CodeFixHarness.ApplyAsync(
            WithLineEndings(PipelineExemptionCodeFixTests.Prelude +
                            "[PipelineExemption(typeof(Ns.MyBehavior<Cmd, CommandResult>))]\n" +
                            "public sealed class Cmd : CommandBase;\n", eol),
            new PipelineExemptionAnalyzer(), new PipelineExemptionCodeFixProvider(), "CQRA008"),

        ["CQRA014: AddCqrs becomes AddCqrsGenerated"] = eol => CodeFixHarness.ApplyAsync(
            WithLineEndings(DirectCoreRegistrationCodeFixTests.Usings +
                            "public class Startup\n" +
                            "{\n" +
                            "    public void Configure(IServiceCollection services) => services.AddCqrs();\n" +
                            "}\n", eol),
            new DirectCoreRegistrationAnalyzer(), new DirectCoreRegistrationCodeFixProvider(), "CQRA014", withGeneratedCode: true),

        ["CQRA018: a chain gets a link"] = eol => MarkerFix("services.AddCqrsGenerated(b => b.UseLogging())", "CQRA018", eol),

        ["CQRA018: a chain laid out one link per line gets a line"] = eol => MarkerFix(
            "services.AddCqrsGenerated(b => b\n" +
            "            .UseLogging()\n" +
            "            .ValidateOnStart())", "CQRA018", eol),

        ["CQRA018: a statement lambda gets a statement"] = eol => MarkerFix(
            "services.AddCqrsGenerated(b =>\n" +
            "        {\n" +
            "            b.UseLogging();\n" +
            "        })", "CQRA018", eol),

        ["CQRA018: an empty statement lambda gets a statement"] = eol => MarkerFix(
            "services.AddCqrsGenerated(b =>\n" +
            "        {\n" +
            "        })", "CQRA018", eol),

        ["CQRA018: the parameterless call gets a configuration"] = eol => MarkerFix("services.AddCqrsGenerated()", "CQRA018", eol),

        ["CQRA019: a chain gets UseResilience"] = eol => MarkerFix("services.AddCqrsGenerated(b => b.UseLogging())", "CQRA019", eol),

        ["CQRA020: the notification gets a name"] = eol => UnnamedHandledNotificationAnalyzerTests.ApplyFixToAsync(
            WithLineEndings(UnnamedHandledNotificationAnalyzerTests.FixInput(UnnamedHandledNotificationAnalyzerTests.Notifications), eol))
    };

    public static TheoryData<string, string> Cases()
    {
        var cases = new TheoryData<string, string>();
        foreach (var fix in Fixes.Keys)
        {
            cases.Add(fix, "LF");
            cases.Add(fix, "CRLF");
        }

        return cases;
    }

    [Theory(DisplayName = "A code fix keeps the line endings of the file it fixes")]
    [MemberData(nameof(Cases))]
    public async Task Fix_keeps_the_files_line_endings(string fix, string lineEnding)
    {
        var eol = lineEnding == "CRLF" ? "\r\n" : "\n";

        var result = await Fixes[fix](eol);

        result.Actions.Should().NotBeEmpty("the fix is offered");
        result.CompileErrors.Should().BeEmpty();
        if (eol == "\n")
            result.FixedSource.Should().NotContain("\r", "every line of an LF file ends in LF");
        else
            result.FixedSource!.Replace("\r\n", "").Should().NotContain("\n", "every line of a CRLF file ends in CRLF");
    }

    private static Task<CodeFixResult> MarkerFix(string registration, string id, string eol)
        => MarkerWithoutBehaviorCodeFixTests.ApplyToAsync(WithLineEndings(MarkerWithoutBehaviorCodeFixTests.Input(registration, id), eol), id);

    private static string WithLineEndings(string source, string eol) => source.Replace("\r\n", "\n").Replace("\n", eol);
}
