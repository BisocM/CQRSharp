using System.Diagnostics;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Core.Pipelines;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Pipelines.Behaviors.Logging;

/// <summary>
///     The streaming counterpart of <see cref="LoggingBehavior{TRequest, TResult}" />: logs the start, item count and
///     elapsed time of a streamed request, or its failure.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TItem">The streamed element type.</typeparam>
public sealed class StreamLoggingBehavior<TRequest, TItem>(
    ILogger<StreamLoggingBehavior<TRequest, TItem>> logger) : IStreamPipelineBehavior<TRequest, TItem>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    public int PipelineExecutionPriority => CqrsPipelinePriorities.Logging;

    public IAsyncEnumerable<TItem> Handle(
        TRequest request,
        Func<CancellationToken, IAsyncEnumerable<TItem>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        return ExecuteAsync();

        async IAsyncEnumerable<TItem> ExecuteAsync()
        {
            var requestName = typeof(TRequest).Name;
            var startTimestamp = Stopwatch.GetTimestamp();
            logger.LogInformation("Streaming {RequestName}", requestName);

            var count = 0L;
            await using var enumerator = next(cancellationToken).GetAsyncEnumerator(cancellationToken);

            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Streaming {RequestName} failed after {Count} item(s) and {ElapsedMs:0.##}ms",
                        requestName, count, Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
                    throw;
                }

                if (!moved) break;
                count++;
                yield return enumerator.Current;
            }

            logger.LogInformation("Streamed {RequestName}: {Count} item(s) in {ElapsedMs:0.##}ms",
                requestName, count, Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }
    }
}
