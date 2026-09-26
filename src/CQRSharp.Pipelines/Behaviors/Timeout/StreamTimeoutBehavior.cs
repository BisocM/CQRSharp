using System.Diagnostics;
using CQRSharp.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Pipelines;

/// <summary>
///     Bounds a streaming request's whole enumeration by <see cref="TimeoutOptions.Timeout" />: when the time runs out it
///     cancels the token the stream receives and, once the stream observes the cancellation, throws
///     <see cref="RequestTimeoutException" />. Like <see cref="TimeoutBehavior{TRequest, TResult}" />, the timeout is
///     cooperative.
/// </summary>
/// <typeparam name="TRequest">The streaming request type.</typeparam>
/// <typeparam name="TItem">The streamed element type.</typeparam>
public sealed class StreamTimeoutBehavior<TRequest, TItem>(
    ILogger<StreamTimeoutBehavior<TRequest, TItem>> logger,
    IOptions<TimeoutOptions> options,
    TimeProvider? timeProvider = null)
    : IStreamPipelineBehavior<TRequest, TItem>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public int PipelineExecutionPriority => CqrsPipelinePriorities.Timeout;

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
            using var activity = PipelineTelemetry.StartActivity<TRequest>("Timeout.Guard");
            var timeout = options.Value.Timeout;
            activity?.SetTag(CqrsTelemetry.Tags.TimeoutMilliseconds, timeout.TotalMilliseconds);

            using var timeoutCancellationTokenSource = new CancellationTokenSource(timeout, _timeProvider);
            using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCancellationTokenSource.Token);
            var combinedCancellationToken = linkedCancellationTokenSource.Token;

            var enumerator = next(combinedCancellationToken).GetAsyncEnumerator(combinedCancellationToken);
            await using (enumerator.ConfigureAwait(false))
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        // Each step resumes in the consumer's flow: the span is made current again for the steps below it.
                        if (activity is not null) Activity.Current = activity;
                        moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException ex) when (timeoutCancellationTokenSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        TimeoutLog.StreamTimedOut(logger, typeof(TRequest).Name, timeout.TotalMilliseconds);
                        activity?.SetStatus(ActivityStatusCode.Error, "Stream timed out.");
                        throw new RequestTimeoutException(typeof(TRequest), timeout, ex);
                    }

                    if (!moved) break;
                    yield return enumerator.Current;
                }
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
        }
    }
}
