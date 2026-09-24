using Microsoft.Extensions.Logging;

namespace CQRSharp.Pipelines;

/// <summary>
///     The streaming counterpart of <see cref="LoggingBehavior{TRequest, TResult}" />: logs the start, item count and
///     elapsed time of a streamed request, or how it ended otherwise: an unexpected failure at Error, with the exception;
///     a rejection, the server being unable to serve it, or its cancellation by the caller in one line below Error, as
///     <see cref="LoggingBehavior{TRequest, TResult}" /> describes.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TItem">The streamed element type.</typeparam>
public sealed class StreamLoggingBehavior<TRequest, TItem>(
    ILogger<StreamLoggingBehavior<TRequest, TItem>> logger, TimeProvider? timeProvider = null) : IStreamPipelineBehavior<TRequest, TItem>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public int PipelineExecutionPriority => CqrsPipelinePriorities.Logging;

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
            var requestName = typeof(TRequest).Name;
            var startTimestamp = _timeProvider.GetTimestamp();
            LoggingBehaviorLog.Streaming(logger, requestName);

            var count = 0L;
            var enumerator = next(cancellationToken).GetAsyncEnumerator(cancellationToken);
            await using (enumerator.ConfigureAwait(false))
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LoggingBehaviorLog.StreamEnded(
                            logger, ex, cancellationToken, requestName, count, _timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds);
                        throw;
                    }

                    if (!moved) break;
                    count++;
                    yield return enumerator.Current;
                }
            }

            LoggingBehaviorLog.Streamed(logger, requestName, count, _timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }
    }
}
