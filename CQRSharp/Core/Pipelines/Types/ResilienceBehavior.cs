using CQRSharp.Core.Options;
using CQRSharp.Core.Pipelines.Types.RateLimiting;
using CQRSharp.Shared.Core.Data.Interfaces.Markers.Request;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Pipelines.Types;

/// <summary>
/// Represents a pipeline behavior that introduces resilience features into the request handling.
/// The behavior implements retry logic based on the configured maximum retry attempts.
/// </summary>
/// <typeparam name="TRequest">The type of the request.</typeparam>
/// <typeparam name="TResult">The type of the result returned after processing the request.</typeparam>
public sealed class ResilienceBehavior<TRequest, TResult>(
    ILogger<ResilienceBehavior<TRequest, TResult>> logger,
    ResilienceOptions options) : IPipelineBehavior<TRequest, TResult>
    where TRequest : IRequest
{
    /// <inheritdoc />
    public async Task<TResult> Handle(TRequest request,
        Func<CancellationToken, Task<TResult>> next, CancellationToken cancellationToken)
    {
        var retries = 0;

        while (true)
            try
            {
                //Attempt to execute the next delegate in the pipeline.
                return await next(cancellationToken);
            }
            catch (RateLimitExceededException rateLimitException)
            {
                //The rate limit has been hit. Don't retry; just propagate the exception.
                logger.LogError(rateLimitException,
                    "Rate limit exceeded for request {RequestName}. No retries will be attempted.",
                    typeof(TRequest).Name);
                throw;
            }
            catch (Exception ex) when (retries < options.MaxRetries)
            {
                //For other exceptions, if we still have retries left, log and retry.
                retries++;
                logger.LogWarning(ex, "Failure executing {RequestName}, retry {RetryCount}/{MaxRetries}",
                    typeof(TRequest).Name, retries, options.MaxRetries);

                //TODO: Says this is "optional", never implements the option to DispatcherOptions. Based?
                //Add a delay before retrying. This is optional and can be configured in DispatcherOptions.
                const int delayMs = 1000;
                await Task.Delay(delayMs, cancellationToken);
            }
            catch (Exception ex)
            {
                // If we get here, we've run out of retries or encountered a non-rate-limited exception with no retries left.
                logger.LogError(ex, "All retries exhausted for request {RequestName}.", typeof(TRequest).Name);
                throw;
            }
    }
}