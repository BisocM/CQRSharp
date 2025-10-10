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
    IOptions<ResilienceOptions> options) : IPipelineBehavior<TRequest, TResult>
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
            catch (Exception ex) when (retries < options.Value.MaxRetries)
            {
                //For other exceptions, if we still have retries left, log and retry.
                retries++;
                logger.LogWarning(ex, "Failure executing {RequestName}, retry {RetryCount}/{MaxRetries}",
                    typeof(TRequest).Name, retries, options.Value.MaxRetries);

                // Records each retry attempt as an event within the activity's timeline.
                var eventTags = new ActivityTagsCollection { { "exception.type", ex.GetType().Name } };
                activity?.AddEvent(new ActivityEvent($"RetryAttempt-{retries}", tags: eventTags));

                //TODO: Says this is "optional", never implements the option to DispatcherOptions. Based?
                //Add a delay before retrying. This is optional and can be configured in DispatcherOptions.
                const int delayMs = 1000;
                await Task.Delay(delayMs, cancellationToken);
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
}