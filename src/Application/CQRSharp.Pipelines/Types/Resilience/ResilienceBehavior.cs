using System.Diagnostics;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines.Options;
using CQRSharp.Pipelines.Telemetry;
using CQRSharp.Pipelines.Types.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Pipelines.Types.Resilience;

/// <summary>
///     Represents a pipeline behavior that introduces resilience features into the request handling.
///     The behavior implements retry logic based on the configured maximum retry attempts.
/// </summary>
/// <typeparam name="TRequest">The type of the request.</typeparam>
/// <typeparam name="TResult">The type of the result returned after processing the request.</typeparam>
public sealed class ResilienceBehavior<TRequest, TResult>(
    ILogger<ResilienceBehavior<TRequest, TResult>> logger,
    IOptions<ResilienceOptions> options) : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    public int PipelineExecutionPriority => 200;

    /// <inheritdoc />
    public async Task<TResult> Handle(TRequest request,
        Func<CancellationToken, Task<TResult>> next, CancellationToken cancellationToken)
    {
        // Creates a trace activity that spans the entire resilience operation.
        using var activity = PipelineTelemetry.StartActivity("Resilience.Operation", request);
        activity?.SetTag("resilience.max_retries", options.Value.MaxRetries);

        var retries = 0;

        while (true)
            try
            {
                //Attempt to execute the next delegate in the pipeline.
                var result = await next(cancellationToken);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return result;
            }
            catch (RateLimitExceededException rateLimitException)
            {
                //The rate limit has been hit. Don't retry; just propagate the exception.
                logger.LogError(rateLimitException,
                    "Rate limit exceeded for request {RequestName}. No retries will be attempted.",
                    typeof(TRequest).Name);

                // Mark the activity as failed before re-throwing the exception.
                activity?.SetStatus(ActivityStatusCode.Error, "Rate limit exceeded.");
                throw;
            }
            catch (Exception ex) when (retries < options.Value.MaxRetries)
            {
                //For other exceptions, if we still have retries left, log and retry.
                retries++;
                logger.LogWarning(ex, "Failure executing {RequestName}, retry {RetryCount}/{MaxRetries}",
                    typeof(TRequest).Name, retries, options.Value.MaxRetries);

                // Records each retry attempt as an event within the activity's timeline.
                var eventTags = new ActivityTagsCollection { { "exception.type", ex.GetType().Name } };
                activity?.AddEvent(new ActivityEvent($"RetryAttempt-{retries}", tags: eventTags));

                var delay = ComputeRetryDelay(options.Value, retries);
                activity?.SetTag("resilience.retry_delay_ms", delay.TotalMilliseconds);

                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                // If we get here, we've run out of retries or encountered a non-rate-limited exception with no retries left.
                logger.LogError(ex, "All retries exhausted for request {RequestName}.", typeof(TRequest).Name);

                // Mark the activity as failed, indicating all retries were exhausted.
                activity?.SetStatus(ActivityStatusCode.Error, "All retries exhausted.");
                throw;
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
