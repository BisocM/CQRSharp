using Microsoft.Extensions.Logging;

namespace CQRSharp.Pipelines;

/// <summary>
///     The streaming counterpart of <see cref="RateLimitingBehavior{TRequest, TResult}" />: takes the caller's token when
///     enumeration starts, and rejects the stream with <see cref="RateLimitExceededException" /> when there is none.
///     Applies only to requests whose context implements <see cref="IRateLimitedContext" />.
/// </summary>
/// <typeparam name="TRequest">The streaming request type.</typeparam>
/// <typeparam name="TItem">The streamed element type.</typeparam>
public sealed class StreamRateLimitingBehavior<TRequest, TItem>(
    ILogger<StreamRateLimitingBehavior<TRequest, TItem>> logger,
    RequestRateLimiter rateLimiter)
    : IStreamPipelineBehavior<TRequest, TItem>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    /// <inheritdoc />
    public int PipelineExecutionPriority => CqrsPipelinePriorities.RateLimiting;

    /// <inheritdoc />
    public IAsyncEnumerable<TItem> Handle(
        TRequest request,
        StreamHandlerDelegate<TItem> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(next);

        return ExecuteAsync();

        async IAsyncEnumerable<TItem> ExecuteAsync()
        {
            RateLimitEnforcement.Enforce(request, rateLimiter, logger);

            await foreach (var item in next(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
    }
}
