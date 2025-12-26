using System.Diagnostics;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines.Options;
using CQRSharp.Pipelines.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Pipelines.Types.Timeout;

/// <summary>
///     Enforces a timeout across the full enumeration of a streaming request.
/// </summary>
public sealed class StreamTimeoutBehavior<TRequest, TItem>(
    ILogger<StreamTimeoutBehavior<TRequest, TItem>> logger,
    IOptions<TimeoutOptions> options)
    : IStreamPipelineBehavior<TRequest, TItem>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    public int PipelineExecutionPriority => 300;

    public IAsyncEnumerable<TItem> Handle(
        TRequest request,
        Func<CancellationToken, IAsyncEnumerable<TItem>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(next);

        return ExecuteAsync();

        async IAsyncEnumerable<TItem> ExecuteAsync()
        {
            using var activity = PipelineTelemetry.StartActivity("Timeout.Guard", request);
            var timeout = options.Value.Timeout;

            activity?.SetTag("cqrsharp.timeout_ms", timeout.TotalMilliseconds);

            using var timeoutCancellationTokenSource = new CancellationTokenSource(timeout);
            using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCancellationTokenSource.Token);
            var combinedCancellationToken = linkedCancellationTokenSource.Token;

            logger.LogInformation(
                "Timeout for {ReqName} set for {TimeoutMilliseconds}ms",
                request.GetType().Name,
                timeout.TotalMilliseconds);

            await using var enumerator = next(combinedCancellationToken).GetAsyncEnumerator(combinedCancellationToken);

            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCancellationTokenSource.IsCancellationRequested)
                {
                    logger.LogError("{RequestName} stream timed out", typeof(TRequest).Name);
                    activity?.SetStatus(ActivityStatusCode.Error, "Stream timed out.");
                    throw new TimeoutException($"{typeof(TRequest).Name} stream timed out.");
                }

                if (!moved) break;
                yield return enumerator.Current;
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
        }
    }
}
