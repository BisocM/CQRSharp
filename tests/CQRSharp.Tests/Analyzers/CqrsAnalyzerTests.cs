using System.Collections.Immutable;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Analyzers;
using CQRSharp.Core.Mediation;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CQRSharp.Tests.Analyzers;

/// <summary>
///     Verifies the CQRSharp analyzers by compiling snippets in-memory against the real CQRSharp assemblies and
///     asserting the reported diagnostics.
/// </summary>
public class CqrsAnalyzerTests
{
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
            "AnalyzerUnderTest",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var compileErrors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (compileErrors.Length > 0)
            throw new InvalidOperationException("Test snippet failed to compile: " + string.Join(" | ", compileErrors.Select(e => e.ToString())));

        var withAnalyzers = compilation.WithAnalyzers(analyzers.ToImmutableArray());
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    [Fact(DisplayName = "CQRA004: Send of a stream request is flagged")]
    public async Task CQRA004_FlagsSendOfStreamRequest()
    {
        const string source = """
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

        var diagnostics = await AnalyzeAsync(source, new SendStreamRequestAnalyzer());
        diagnostics.Should().ContainSingle(d => d.Id == "CQRA004" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact(DisplayName = "CQRA004: Send of a command is not flagged")]
    public async Task CQRA004_DoesNotFlagSendOfCommand()
    {
        const string source = """
                              using System.Threading.Tasks;
                              using CQRSharp.Abstractions.Interfaces.Context;
                              using CQRSharp.Abstractions.Interfaces.Markers.Command;
                              using CQRSharp.Abstractions.Models.Requests;
                              using CQRSharp.Core.Mediation;

                              public sealed class MyCommand : ICommand
                              {
                                  public IRequestContext? Context { get; set; }
                                  public RequestMetadata? Metadata { get; set; }
                              }

                              public class Consumer
                              {
                                  public async Task Run(ICqrsDispatcher dispatcher) => await dispatcher.Send(new MyCommand());
                              }
                              """;

        var diagnostics = await AnalyzeAsync(source, new SendStreamRequestAnalyzer());
        diagnostics.Should().NotContain(d => d.Id == "CQRA004");
    }

    [Fact(DisplayName = "CQRA005: PipelineExemption of a non-behavior is flagged")]
    public async Task CQRA005_FlagsNonBehaviorExemption()
    {
        const string source = """
                              using CQRSharp.Abstractions.Attributes.Pipelines;

                              public sealed class NotABehavior { }

                              [PipelineExemption(typeof(NotABehavior))]
                              public sealed class SomeRequest { }
                              """;

        var diagnostics = await AnalyzeAsync(source, new PipelineExemptionAnalyzer());
        diagnostics.Should().ContainSingle(d => d.Id == "CQRA005" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact(DisplayName = "CQRA003: Send of a request with no handler is flagged")]
    public async Task CQRA003_FlagsRequestWithNoHandler()
    {
        const string source = """
                              using System.Threading.Tasks;
                              using CQRSharp.Abstractions.Interfaces.Context;
                              using CQRSharp.Abstractions.Interfaces.Markers.Command;
                              using CQRSharp.Abstractions.Models.Requests;
                              using CQRSharp.Core.Mediation;

                              public sealed class Unhandled : ICommand
                              {
                                  public IRequestContext? Context { get; set; }
                                  public RequestMetadata? Metadata { get; set; }
                              }

                              public class Consumer
                              {
                                  public async Task Run(ICqrsDispatcher d) => await d.Send(new Unhandled());
                              }
                              """;

        var diagnostics = await AnalyzeAsync(source, new HandlerDiscoveryAnalyzer());
        diagnostics.Should().ContainSingle(d => d.Id == "CQRA003");
    }

    [Fact(DisplayName = "CQRA003: Send of a request with a handler is not flagged")]
    public async Task CQRA003_DoesNotFlagRequestWithHandler()
    {
        const string source = """
                              using System.Threading;
                              using System.Threading.Tasks;
                              using CQRSharp.Abstractions.Interfaces.Context;
                              using CQRSharp.Abstractions.Interfaces.Handlers;
                              using CQRSharp.Abstractions.Interfaces.Markers.Command;
                              using CQRSharp.Abstractions.Models.Commands;
                              using CQRSharp.Abstractions.Models.Requests;
                              using CQRSharp.Core.Mediation;

                              public sealed class Handled : ICommand
                              {
                                  public IRequestContext? Context { get; set; }
                                  public RequestMetadata? Metadata { get; set; }
                              }

                              public sealed class HandledHandler : ICommandHandler<Handled>
                              {
                                  public Task<CommandResult> Handle(Handled command, CancellationToken cancellationToken)
                                      => Task.FromResult(CommandResult.FromSuccess());
                              }

                              public class Consumer
                              {
                                  public async Task Run(ICqrsDispatcher d) => await d.Send(new Handled());
                              }
                              """;

        var diagnostics = await AnalyzeAsync(source, new HandlerDiscoveryAnalyzer());
        diagnostics.Should().NotContain(d => d.Id == "CQRA003");
    }

    [Fact(DisplayName = "CQRA006: Publish of a notification with no subscriber is flagged")]
    public async Task CQRA006_FlagsNotificationWithNoSubscriber()
    {
        const string source = """
                              using System.Threading.Tasks;
                              using CQRSharp.Abstractions.Interfaces.Notifications;
                              using CQRSharp.Core.Mediation;

                              public sealed class Unheard : INotification { }

                              public class Consumer
                              {
                                  public async Task Run(ICqrsDispatcher d) => await d.Publish(new Unheard());
                              }
                              """;

        var diagnostics = await AnalyzeAsync(source, new HandlerDiscoveryAnalyzer());
        diagnostics.Should().ContainSingle(d => d.Id == "CQRA006");
    }

    [Fact(DisplayName = "CQRA001: handler context type mismatch is flagged")]
    public async Task CQRA001_FlagsContextMismatch()
    {
        const string source = """
                              using System.Threading;
                              using System.Threading.Tasks;
                              using CQRSharp.Abstractions.Interfaces.Context;
                              using CQRSharp.Abstractions.Interfaces.Handlers;
                              using CQRSharp.Abstractions.Interfaces.Markers.Command;
                              using CQRSharp.Abstractions.Models.Commands;

                              public sealed class CtxA : RequestContextBase { }
                              public sealed class CtxB : RequestContextBase { }

                              public sealed class MismatchedCommand : CommandBase<CtxA> { }

                              public sealed class MismatchedHandler : ICommandHandler<MismatchedCommand, CtxB>
                              {
                                  public Task<CommandResult> Handle(MismatchedCommand command, CancellationToken cancellationToken)
                                      => Task.FromResult(CommandResult.FromSuccess());
                              }
                              """;

        var diagnostics = await AnalyzeAsync(source, new HandlerContextMismatchAnalyzer());
        diagnostics.Should().ContainSingle(d => d.Id == "CQRA001");
    }

    [Fact(DisplayName = "CQRA001: convenience handler for a custom-context request is not flagged")]
    public async Task CQRA001_DoesNotFlagConvenienceHandlerWithCustomContext()
    {
        // The documented convenience overload ICommandHandler<TCommand> inherits ICommandHandler<TCommand,
        // RequestContextBase>. That implicit default context must NOT be reported as a mismatch against a request
        // that declares a custom context — the developer never spelled out a context type argument.
        const string source = """
                              using System.Threading;
                              using System.Threading.Tasks;
                              using CQRSharp.Abstractions.Interfaces.Context;
                              using CQRSharp.Abstractions.Interfaces.Handlers;
                              using CQRSharp.Abstractions.Interfaces.Markers.Command;
                              using CQRSharp.Abstractions.Models.Commands;

                              public sealed class CustomCtx : RequestContextBase { }

                              public sealed class CustomContextCommand : CommandBase<CustomCtx> { }

                              public sealed class ConvenienceHandler : ICommandHandler<CustomContextCommand>
                              {
                                  public Task<CommandResult> Handle(CustomContextCommand command, CancellationToken cancellationToken)
                                      => Task.FromResult(CommandResult.FromSuccess());
                              }
                              """;

        var diagnostics = await AnalyzeAsync(source, new HandlerContextMismatchAnalyzer());
        diagnostics.Should().NotContain(d => d.Id == "CQRA001");
    }

    [Fact(DisplayName = "CQRA005: open-generic behavior exemption is not flagged")]
    public async Task CQRA005_DoesNotFlagOpenGenericBehaviorExemption()
    {
        // typeof(MyBehavior<,>) is an unbound generic; the analyzer must still recognize it as a pipeline behavior.
        const string source = """
                              using System;
                              using System.Threading;
                              using System.Threading.Tasks;
                              using CQRSharp.Abstractions.Attributes.Pipelines;
                              using CQRSharp.Abstractions.Interfaces.Markers.Request;
                              using CQRSharp.Core.Pipelines;

                              public sealed class MyBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult>
                                  where TRequest : IRequest
                              {
                                  public Task<TResult> Handle(TRequest request, Func<CancellationToken, Task<TResult>> next, CancellationToken cancellationToken)
                                      => next(cancellationToken);
                              }

                              [PipelineExemption(typeof(MyBehavior<,>))]
                              public sealed class SomeRequest { }
                              """;

        var diagnostics = await AnalyzeAsync(source, new PipelineExemptionAnalyzer());
        diagnostics.Should().NotContain(d => d.Id == "CQRA005");
    }
}