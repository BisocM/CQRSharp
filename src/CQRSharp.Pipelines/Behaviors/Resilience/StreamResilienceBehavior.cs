using System.Diagnostics;
using System.Runtime.ExceptionServices;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Pipelines;

/// <summary>
///     The streaming counterpart of <see cref="ResilienceBehavior{TRequest, TResult}" />: retries a streaming request
///     implementing <see cref="IRetryableRequest" /> that throws an exception another attempt might change before
///     yielding any item (after one, a retry would deliver items twice). Any other streaming request passes straight through, untouched.
/// </summary>
/// <typeparam name="TRequest">The streaming request type.</typeparam>
/// <typeparam name="TItem">The streamed element type.</typeparam>
public sealed class StreamResilienceBehavior<TRequest, TItem>(
    ILogger<StreamResilienceBehavior<TRequest, TItem>> logger,
    IOptions<ResilienceOptions> options,
    TimeProvider? timeProvider = null)
    : IStreamPipelineBehavior<TRequest, TItem>, IPrioritizedPipelineBehavior, ICqrsResilienceBehaviorMarker
    where TRequest : IRequest
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    // Runs outside the unit of work, so each retry executes against a fresh transaction.
    /// <inheritdoc />
    public int PipelineExecutionPriority => CqrsPipelinePriorities.Resilience;

    /// <inheritdoc />
    public IAsyncEnumerable<TItem> Handle(
        TRequest request,
        StreamHandlerDelegate<TItem> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(next);

        return request is IRetryableRequest ? ExecuteWithRetries() : next(cancellationToken);

        async IAsyncEnumerable<TItem> ExecuteWithRetries()
        {
            var settings = options.Value;
            using var activity = PipelineTelemetry.StartActivity<TRequest>("Resilience.Operation");
            activity?.SetTag(CqrsTelemetry.Tags.MaxRetries, settings.MaxRetries);

            var retries = 0;

            while (true)
            {
                var yieldedAny = false;
                Exception? failure = null;

                var enumerator = next(cancellationToken).GetAsyncEnumerator(cancellationToken);
                await using (enumerator.ConfigureAwait(false))
                {
                    while (true)
                    {
                        bool moved;
                        try
                        {
                            // Each step resumes in the consumer's flow: the span is made current again for the steps below
                            // it, a retry attempt's included.
                            if (activity is not null) Activity.Current = activity;
                            moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                            break;
                        }

                        if (!moved) break;
                        yieldedAny = true;
                        yield return enumerator.Current;
                    }
                }

                if (failure is null)
                {
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    yield break;
                }

                if (RetryPolicy.IsTerminal(failure, cancellationToken, out var reason))
                {
                    ResilienceLog.NotRetried(logger, typeof(TRequest).Name, reason);
                    activity?.SetStatus(ActivityStatusCode.Error, reason);
                    ExceptionDispatchInfo.Capture(failure).Throw();
                }

                if (yieldedAny)
                {
                    ResilienceLog.StreamFailedAfterItems(logger, typeof(TRequest).Name);
                    activity?.SetStatus(ActivityStatusCode.Error, "Stream faulted after yielding items.");
                    ExceptionDispatchInfo.Capture(failure).Throw();
                }

                if (retries >= settings.MaxRetries)
                {
                    ResilienceLog.RetriesExhausted(logger, typeof(TRequest).Name, retries + 1);
                    activity?.SetStatus(ActivityStatusCode.Error, "All retries exhausted.");
                    ExceptionDispatchInfo.Capture(failure).Throw();
                }

                retries++;
                var delay = settings.ComputeRetryDelay(retries);
                ResilienceLog.Retrying(logger, failure, typeof(TRequest).Name, retries, settings.MaxRetries, delay.TotalMilliseconds);

                var eventTags = new ActivityTagsCollection { { "exception.type", failure.GetType().Name } };
                activity?.AddEvent(new ActivityEvent($"RetryAttempt-{retries}", tags: eventTags));
                activity?.SetTag(CqrsTelemetry.Tags.RetryDelayMilliseconds, delay.TotalMilliseconds);

                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
