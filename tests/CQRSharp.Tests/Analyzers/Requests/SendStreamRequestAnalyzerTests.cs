using CQRSharp.Analyzers;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.CodeAnalysis;

namespace CQRSharp.Tests.Analyzers;

/// <summary>CQRA004: a stream request dispatched with <c>Send(...)</c>.</summary>
public sealed class SendStreamRequestAnalyzerTests
{
    [Fact(DisplayName = "CQRA004: Send of a stream request is flagged")]
    public async Task Send_of_a_stream_request_is_flagged()
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync("""
            using System.Threading.Tasks;
            using CQRSharp;

            public sealed class MyStream : StreamRequestBase<int>;
            public class Consumer
            {
                public async Task Run(ICqrsDispatcher dispatcher) => await dispatcher.Send(new MyStream());
            }
            """, new SendStreamRequestAnalyzer());

        diagnostics.Should().ContainSingle(d => d.Id == "CQRA004" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact(DisplayName = "CQRA004: Send of a command is not flagged")]
    public async Task Send_of_a_command_is_not_flagged()
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync("""
            using System.Threading.Tasks;
            using CQRSharp;

            public sealed class MyCommand : CommandBase;
            public class Consumer
            {
                public async Task Run(ICqrsDispatcher dispatcher) => await dispatcher.Send(new MyCommand());
            }
            """, new SendStreamRequestAnalyzer());

        diagnostics.Should().NotContain(d => d.Id == "CQRA004");
    }

    [Fact(DisplayName = "CQRA004 and CQRA003 see the request when named arguments put the token first")]
    public async Task Reordered_named_arguments_are_analyzed()
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync("""
            using System.Threading;
            using System.Threading.Tasks;
            using CQRSharp;

            public sealed class Names : StreamRequestBase<string>;
            public sealed class Unhandled : CommandBase;
            public static class Use
            {
                public static Task Run(ICqrsDispatcher d, CancellationToken ct)
                    => Task.WhenAll(d.Send(cancellationToken: ct, request: new Names()), d.Send(cancellationToken: ct, request: new Unhandled()));
            }
            """, new SendStreamRequestAnalyzer(), new HandlerDiscoveryAnalyzer());

        diagnostics.Should().Contain(d => d.Id == "CQRA004");
        diagnostics.Should().Contain(d => d.Id == "CQRA003" && d.GetMessage().Contains("Unhandled"));
    }
}
