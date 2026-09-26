using CQRSharp.Analyzers;
using FluentAssertions;

namespace CQRSharp.Tests.Analyzers;

/// <summary>
///     The CQRA008 code fix: a closed-generic exemption that exempts the behavior for its request becomes the open form.
///     Each case compiles the fixed project.
/// </summary>
public sealed class PipelineExemptionCodeFixTests
{
    private const string Prelude = """
                                   using System.Threading;
                                   using System.Threading.Tasks;
                                   using CQRSharp;
                                   using CQRSharp.Pipelines;

                                   namespace Ns
                                   {
                                       public sealed class MyBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
                                       {
                                           public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
                                       }
                                       public sealed class Tri<TRequest, TResult, TExtra> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
                                       {
                                           public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
                                       }
                                       public static class Outer
                                       {
                                           public sealed class Inner<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
                                           {
                                               public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
                                           }
                                       }
                                   }

                                   """;

    [Fact(DisplayName = "CQRA008 code fix: the provider fixes CQRA008 only")]
    public void Fixes_cqra008_only()
        => new PipelineExemptionCodeFixProvider().FixableDiagnosticIds.Should().Equal("CQRA008");

    [Theory(DisplayName = "CQRA008 code fix: the closed form becomes the open form of the same behavior, and compiles")]
    [InlineData("Ns.MyBehavior<Cmd, CommandResult>", "Ns.MyBehavior<,>")]
    [InlineData("global::Ns.MyBehavior<global::Cmd, global::CQRSharp.CommandResult>", "global::Ns.MyBehavior<,>")]
    [InlineData("Ns.Tri<Cmd, CommandResult, int>", "Ns.Tri<,,>")]
    [InlineData("Ns.Outer.Inner<Cmd, CommandResult>", "Ns.Outer.Inner<,>")]
    public async Task Closed_form_becomes_open(string closed, string open)
    {
        var result = await CodeFixHarness.ApplyAsync(
            Prelude + "[PipelineExemption(typeof(" + closed + "))] public sealed class Cmd : CommandBase;",
            new PipelineExemptionAnalyzer(), new PipelineExemptionCodeFixProvider(), "CQRA008");

        result.Actions.Should().ContainSingle().Which.Title.Should().EndWith("<" + open.Split('<')[1] + ")");
        result.FixedSource.Should().Contain("[PipelineExemption(typeof(" + open + "))]");
        result.CompileErrors.Should().BeEmpty();
        result.RemainingDiagnostics.Should().BeEmpty();
    }
}
