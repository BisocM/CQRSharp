using System.Diagnostics;
using System.Runtime.ExceptionServices;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines.Options;
using CQRSharp.Pipelines.Telemetry;
using CQRSharp.Pipelines.Types.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Pipelines.Types.Resilience;

/// <summary>
///     Adds retry support for streaming requests. Retries are only attempted when the stream fails
///     before yielding any items to avoid duplicate partial results.
/// </summary>
public sealed class StreamResilienceBehavior<TRequest, TItem>(
    ILogger<StreamResilienceBehavior<TRequest, TItem>> logger,
    IOptions<ResilienceOptions> options)
    : IStreamPipelineBehavior<TRequest, TItem>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    public int PipelineExecutionPriority => 200;

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
            using var activity = PipelineTelemetry.StartActivity("Resilience.Operation", request);
            activity?.SetTag("resilience.max_retries", options.Value.MaxRetries);

            var retries = 0;

            while (true)
            {
                var yieldedAny = false;
                Exception? failure = null;

                await using (var enumerator = next(cancellationToken).GetAsyncEnumerator(cancellationToken))
                {
                    while (true)
                    {
                        bool moved;
                        try
                        {
                            moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                        }
                        catch (RateLimitExceededException ex)
                        {
                            failure = ex;
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
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

                if (failure is RateLimitExceededException rateLimitException)
                {
                    logger.LogError(rateLimitException,
                        "Rate limit exceeded for streaming request {RequestName}. No retries will be attempted.",
                        typeof(TRequest).Name);

                    activity?.SetStatus(ActivityStatusCode.Error, "Rate limit exceeded.");
                    ExceptionDispatchInfo.Capture(failure).Throw();
                    yield break;
                }

                if (yieldedAny)
                {
                    logger.LogError(failure,
                        "Streaming request {RequestName} failed after yielding items; retries are disabled to avoid duplicates.",
                        typeof(TRequest).Name);

                    activity?.SetStatus(ActivityStatusCode.Error, "Stream faulted after yielding items.");
                    ExceptionDispatchInfo.Capture(failure).Throw();
                    yield break;
                }

                if (retries < options.Value.MaxRetries)
                {
                    retries++;
                    logger.LogWarning(failure, "Failure executing streaming request {RequestName}, retry {RetryCount}/{MaxRetries}",
                        typeof(TRequest).Name, retries, options.Value.MaxRetries);

                    var eventTags = new ActivityTagsCollection { { "exception.type", failure.GetType().Name } };
                    activity?.AddEvent(new ActivityEvent($"RetryAttempt-{retries}", tags: eventTags));

                    var delay = ComputeRetryDelay(options.Value, retries);
                    activity?.SetTag("resilience.retry_delay_ms", delay.TotalMilliseconds);

                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                    continue;
                }

                logger.LogError(failure, "All retries exhausted for streaming request {RequestName}.", typeof(TRequest).Name);
                activity?.SetStatus(ActivityStatusCode.Error, "All retries exhausted.");
                ExceptionDispatchInfo.Capture(failure).Throw();
                yield break;
            }
        }
    }

    private static TimeSpan ComputeRetryDelay(ResilienceOptions config, int retryAttempt)
    {
        if (retryAttempt <= 0) return TimeSpan.Zero;

        var baseDelay = config.BaseDelay;
        if (baseDelay <= TimeSpan.Zero) return TimeSpan.Zero;

        var backoffMultiplier = config.BackoffMultiplier;
        if (double.IsNaN(backoffMultiplier) || double.IsInfinity(backoffMultiplier) || backoffMultiplier < 1.0)
            backoffMultiplier = 1.0;

        var delayMs = baseDelay.TotalMilliseconds * Math.Pow(backoffMultiplier, retryAttempt - 1);

        var maxDelay = config.MaxDelay;
        if (maxDelay > TimeSpan.Zero)
            delayMs = Math.Min(delayMs, maxDelay.TotalMilliseconds);

        if (double.IsNaN(delayMs) || double.IsInfinity(delayMs) || delayMs <= 0)
            return baseDelay;

        return TimeSpan.FromMilliseconds(delayMs);
    }
}
