using CQRSharp.Sample.Infrastructure.SelfTest;

namespace CQRSharp.Sample.Application.Pipelines;

/// <summary>An open-generic stream behavior that records every stream it wraps (the self-test checks it ran).</summary>
public sealed class StreamProbeBehavior<TRequest, TItem>(SampleDiagnostics diagnostics) : IStreamPipelineBehavior<TRequest, TItem>
    where TRequest : IRequest
{
    public IAsyncEnumerable<TItem> Handle(TRequest request, StreamHandlerDelegate<TItem> next, CancellationToken cancellationToken)
    {
        diagnostics.RecordStreamBehavior(typeof(TRequest));
        return next(cancellationToken);
    }
}
