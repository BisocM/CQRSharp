using System.Diagnostics;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines.Behaviors.RateLimiting;
using CQRSharp.Pipelines.Options;
using CQRSharp.Pipelines.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Pipelines.Behaviors.Resilience;

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
            catch (OperationCanceledException)
            {
                // Caller-initiated cancellation is terminal: never retry it (mirrors the streaming variant).
                activity?.SetStatus(ActivityStatusCode.Error, "Operation canceled.");
                throw;
            }
            catch (TimeoutException timeoutException)
            {
                // A timeout means the attempt already exceeded its time budget; retrying would multiply the budget by
                // the retry count. Treat it as terminal and propagate it.
                logger.LogError(timeoutException,
                    "Request {RequestName} timed out. No retries will be attempted.", typeof(TRequest).Name);
                activity?.SetStatus(ActivityStatusCode.Error, "Timed out.");
                throw;
            }
            catch (Exception ex) when (request is IRetryableRequest && retries < options.Value.MaxRetries)
            {
                //For retryable requests with retries remaining, log and retry.
                retries++;
                logger.LogWarning(ex, "Failure executing {RequestName}, retry {RetryCount}/{MaxRetries}",
                    typeof(TRequest).Name, retries, options.Value.MaxRetries);

                // Records each retry attempt as an event within the activity's timeline.
                var eventTags = new ActivityTagsCollection { { "exception.type", ex.GetType().Name } };
                activity?.AddEvent(new ActivityEvent($"RetryAttempt-{retries}", tags: eventTags));

                var delay = options.Value.ComputeRetryDelay(retries);
                activity?.SetTag("resilience.retry_delay_ms", delay.TotalMilliseconds);

                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                // We get here when the request is not retryable, or its retries are exhausted.
                logger.LogError(ex, "Request {RequestName} failed and will not be retried further.", typeof(TRequest).Name);
                activity?.SetStatus(ActivityStatusCode.Error, "Request failed; no further retries.");
                throw;
            }
    }

    // Runs OUTSIDE the unit-of-work behavior (priority 100) so that each retry executes against a fresh transaction
    // rather than replaying work against an already-aborted one.
    public int PipelineExecutionPriority => CqrsPipelinePriorities.Resilience;
}