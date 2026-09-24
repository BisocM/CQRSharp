using CQRSharp.Analyzers;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.CodeAnalysis;

namespace CQRSharp.Tests.Analyzers;

/// <summary>
///     CQRA005 (a <c>[PipelineExemption]</c> that can never take effect) and CQRA008 (a closed form that can be written
///     open). An exemption is read from the dispatched request and matched against the concrete behavior type that runs
///     for it, or that type's open-generic definition.
/// </summary>
public sealed class PipelineExemptionAnalyzerTests
{
    private const string Prelude = """
                                  using System;
                                  using System.Collections.Generic;
                                  using System.Threading;
                                  using System.Threading.Tasks;
                                  using CQRSharp;
                                  using CQRSharp.Pipelines;

                                  public sealed class MyBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
                                  {
                                      public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
                                  }
                                  public sealed class MyStreamBehavior<TRequest, TItem> : IStreamPipelineBehavior<TRequest, TItem> where TRequest : IRequest
                                  {
                                      public IAsyncEnumerable<TItem> Handle(TRequest request, StreamHandlerDelegate<TItem> next, CancellationToken cancellationToken) => next(cancellationToken);
                                  }
                                  public sealed class Other : CommandBase;

                                  """;

    [Fact(DisplayName = "CQRA005: an exemption of a type that is not a pipeline behavior is flagged")]
    public async Task Non_behavior_is_flagged()
    {
        var diagnostic = await SingleAsync(Prelude + """
            public sealed class NotABehavior;
            [PipelineExemption(typeof(NotABehavior))]
            public sealed class Cmd : CommandBase;
            """, "CQRA005");

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
        diagnostic.GetMessage().Should().Contain("'NotABehavior' is not a pipeline behavior");
    }

    [Theory(DisplayName = "CQRA005/CQRA008: an exemption that applies to its request is not flagged")]
    [InlineData("[PipelineExemption(typeof(MyBehavior<,>))] public sealed class Cmd : CommandBase;")]
    [InlineData("[PipelineExemption(typeof(MyStreamBehavior<,>))] public sealed class Numbers : StreamRequestBase<int>;")]
    [InlineData("[PipelineExemption(typeof(MyBehavior<,>))] public abstract class AuditedCommand : CommandBase;")]
    [InlineData("[PipelineExemption(typeof(MyBehavior<,>))] public abstract class AuditedBase;")]
    [InlineData("public sealed class AnyCommandBehavior : IPipelineBehavior<ICommand, CommandResult> { public Task<CommandResult> Handle(ICommand request, RequestHandlerDelegate<CommandResult> next, CancellationToken cancellationToken) => next(cancellationToken); } [PipelineExemption(typeof(AnyCommandBehavior))] public sealed class Cmd : CommandBase;")]
    public async Task Effective_exemption_is_not_flagged(string declarations)
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync(Prelude + declarations, new PipelineExemptionAnalyzer());

        diagnostics.Should().NotContain(d => d.Id == "CQRA005" || d.Id == "CQRA008");
    }

    [Theory(DisplayName = "CQRA005: an exemption on a type that is not a request is flagged")]
    [InlineData("[PipelineExemption(typeof(MyBehavior<,>))] public sealed class NotARequest;", "'NotARequest' is not a request")]
    [InlineData("public sealed class Cmd : CommandBase; [PipelineExemption(typeof(MyBehavior<,>))] public class CmdHandler : ICommandHandler<Cmd> { public Task<CommandResult> Handle(Cmd command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess()); }", "'CmdHandler' is a handler")]
    public async Task Exemption_off_a_request_is_flagged(string declarations, string reason)
    {
        var diagnostic = await SingleAsync(Prelude + declarations, "CQRA005");

        diagnostic.GetMessage().Should().Contain(reason);
    }

    [Theory(DisplayName = "CQRA005: an exemption naming an abstract behavior or a behavior interface is flagged")]
    [InlineData("public abstract class BaseBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest { public abstract Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken); } [PipelineExemption(typeof(BaseBehavior<,>))] public sealed class Cmd : CommandBase;", "is abstract")]
    [InlineData("public interface IAuditBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest; [PipelineExemption(typeof(IAuditBehavior<,>))] public sealed class Cmd : CommandBase;", "is an interface")]
    public async Task Abstract_behavior_is_flagged(string declarations, string reason)
    {
        var diagnostic = await SingleAsync(Prelude + declarations, "CQRA005");

        diagnostic.GetMessage().Should().Contain(reason);
    }

    [Theory(DisplayName = "CQRA005: a behavior that never runs for the request is flagged, not offered as the open form")]
    [InlineData("[PipelineExemption(typeof(MyBehavior<Other, CommandResult>))] public sealed class Cmd : CommandBase;", "closed over other type arguments")]
    [InlineData("[PipelineExemption(typeof(MyBehavior<Cmd, int>))] public sealed class Cmd : CommandBase;", "closed over other type arguments")]
    [InlineData("[PipelineExemption(typeof(MyStreamBehavior<,>))] public sealed class Cmd : CommandBase;", "never runs for 'Cmd', which is not a stream request")]
    [InlineData("public sealed class OtherAudit : IPipelineBehavior<Other, CommandResult> { public Task<CommandResult> Handle(Other request, RequestHandlerDelegate<CommandResult> next, CancellationToken cancellationToken) => next(cancellationToken); } [PipelineExemption(typeof(OtherAudit))] public sealed class Cmd : CommandBase;", "'OtherAudit' never runs for 'Cmd'")]
    public async Task Behavior_that_never_runs_is_flagged(string declarations, string reason)
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync(Prelude + declarations, new PipelineExemptionAnalyzer());

        diagnostics.Should().ContainSingle(d => d.Id == "CQRA005").Which.GetMessage().Should().Contain(reason);
        diagnostics.Should().NotContain(d => d.Id == "CQRA008");
    }

    [Theory(DisplayName = "CQRA008: a closed form that exempts the behavior for its request suggests the open form")]
    [InlineData("[PipelineExemption(typeof(MyBehavior<Cmd, CommandResult>))] public sealed class Cmd : CommandBase;")]
    [InlineData("[PipelineExemption(typeof(MyBehavior<Count, int>))] public sealed class Count : QueryBase<int>;")]
    [InlineData("[PipelineExemption(typeof(MyBehavior<Mint, CommandResult<string>>))] public sealed class Mint : ResultCommandBase<string>;")]
    [InlineData("[PipelineExemption(typeof(MyStreamBehavior<Numbers, int>))] public sealed class Numbers : StreamRequestBase<int>;")]
    public async Task Equivalent_closed_form_suggests_the_open_form(string declarations)
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync(Prelude + declarations, new PipelineExemptionAnalyzer());

        diagnostics.Should().ContainSingle(d => d.Id == "CQRA008" && d.Severity == DiagnosticSeverity.Info)
            .Which.GetMessage().Should().Contain("<,>");
        diagnostics.Should().NotContain(d => d.Id == "CQRA005");
    }

    [Theory(DisplayName = "CQRA005: an exemption a derived request inherits and that runs for it is live on a class other requests derive from")]
    [InlineData("[PipelineExemption(typeof(MyBehavior<DerivedCmd, CommandResult>))] public class BaseCmd : CommandBase; public sealed class DerivedCmd : BaseCmd;")]
    [InlineData("public sealed class DerivedAudit : IPipelineBehavior<DerivedCmd, CommandResult> { public Task<CommandResult> Handle(DerivedCmd request, RequestHandlerDelegate<CommandResult> next, CancellationToken cancellationToken) => next(cancellationToken); } [PipelineExemption(typeof(DerivedAudit))] public class BaseCmd : CommandBase; public sealed class DerivedCmd : BaseCmd;")]
    public async Task Exemption_live_for_a_derived_request_is_not_flagged(string declarations)
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync(Prelude + declarations, new PipelineExemptionAnalyzer());

        diagnostics.Should().NotContain(d => d.Id == "CQRA005" || d.Id == "CQRA008");
    }

    [Theory(DisplayName = "CQRA008: the open form is not offered on a request other requests can derive from, where it would exempt more")]
    [InlineData("[PipelineExemption(typeof(MyBehavior<BaseCmd, CommandResult>))] public class BaseCmd : CommandBase;")]
    [InlineData("[PipelineExemption(typeof(MyBehavior<BaseQuery, int>))] public record BaseQuery : IQuery<int> { public IRequestContext? Context { get; set; } }")]
    public async Task Closed_form_on_a_derivable_request_is_not_rewritten(string declarations)
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync(Prelude + declarations, new PipelineExemptionAnalyzer());

        diagnostics.Should().NotContain(d => d.Id == "CQRA005" || d.Id == "CQRA008");
    }

    [Fact(DisplayName = "CQRA008 is not offered, and nothing crashes, for a non-generic behavior nested in a generic type")]
    public async Task Behavior_nested_in_a_generic_type_is_left_alone()
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync(Prelude + """
            public class Outer<T>
            {
                public sealed class Beh : IPipelineBehavior<Cmd1, CommandResult>
                {
                    public Task<CommandResult> Handle(Cmd1 request, RequestHandlerDelegate<CommandResult> next, CancellationToken cancellationToken) => next(cancellationToken);
                }
            }
            [PipelineExemption(typeof(Outer<int>.Beh))]
            public sealed class Cmd1 : CommandBase;
            """, new PipelineExemptionAnalyzer());

        diagnostics.Should().NotContain(d => d.Id == "CQRA005" || d.Id == "CQRA008");
    }

    private static async Task<Diagnostic> SingleAsync(string source, string id)
        => (await CompilationHarness.AnalyzeAsync(source, new PipelineExemptionAnalyzer())).Should().ContainSingle(d => d.Id == id).Subject;
}
