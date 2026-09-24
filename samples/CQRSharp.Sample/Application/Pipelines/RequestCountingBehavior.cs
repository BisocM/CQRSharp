using CQRSharp.Sample.Infrastructure.SelfTest;

namespace CQRSharp.Sample.Application.Pipelines;

/// <summary>
///     An application-defined open-generic behavior: counts every request it wraps, per request type (the self-test reads
///     the counts). Program.cs registers it after <c>AddCqrsGenerated</c>; under Native AOT it still wraps requests with a
///     value-type result, through the closed factories the source generator emits for it. <c>PingCommand</c> opts out
///     with <c>[PipelineExemption]</c>.
/// </summary>
public sealed class RequestCountingBehavior<TRequest, TResult>(SampleDiagnostics diagnostics)
    : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    // Just inside the built-in logging behavior, so a request counts once however often resilience retries it.
    public int PipelineExecutionPriority => CqrsPipelinePriorities.Logging + 1;

    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken)
    {
        diagnostics.RecordRequest(typeof(TRequest));
        return next(cancellationToken);
    }
}
