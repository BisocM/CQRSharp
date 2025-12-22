using System.Diagnostics;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines.Telemetry;
using CQRSharp.Pipelines.Types.RateLimiting.Context;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Pipelines.Types.RateLimiting;

/// <summary>
///     Represents a pipeline behavior that enforces rate limiting based on request metadata and request context
///     information.
/// </summary>
/// <typeparam name="TRequest">
///     The type of request being processed, which must inherit from RequestBase implementing
///     IRateLimitedContext.
/// </typeparam>
/// <typeparam name="TResult">The type of result returned after request processing.</typeparam>
/// <remarks>
///     This behavior utilizes the provided <see cref="RateLimiter" /> to enforce rate limiting rules based on user
///     identifiers.
///     It extracts the user identifier from the associated <see cref="IRateLimitedContext" /> of the request and checks
///     whether
///     the request is allowed to proceed.
/// </remarks>
/// <example>
///     This behavior should be integrated into a pipeline as part of processing requests.
/// </example>
/// <seealso cref="IPipelineBehavior{TRequest,TResult}" />
public sealed class RateLimitingBehavior<TRequest, TResult>(
    ILogger<RateLimitingBehavior<TRequest, TResult>> logger,
    RateLimiter rateLimiter)
    : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior where TRequest : IRequest
{
    public int PipelineExecutionPriority => int.MinValue;

    /// <inheritdoc />
    public async Task<TResult> Handle(TRequest request,
        Func<CancellationToken, Task<TResult>> next, CancellationToken cancellationToken)
    {
        // Creates a trace activity for the rate limiting check.
        using var activity = PipelineTelemetry.StartActivity("RateLimiting.Check", request);
        ArgumentNullException.ThrowIfNull(request);

        // Rate limiting is explicitly enabled by providing a context that implements IRateLimitedContext.
        if (request.Context is not IRateLimitedContext rateLimitedContext)
        {
            activity?.SetStatus(ActivityStatusCode.Ok, "Rate limiting skipped (no IRateLimitedContext).");
            return await next(cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(rateLimitedContext.UserId) ||
            string.IsNullOrWhiteSpace(rateLimitedContext.RequestId))
        {
            logger.LogError("Rate limiting failed: UserId/RequestId missing from IRateLimitedContext.");
            // Mark the activity as failed due to missing identifiers.
            activity?.SetStatus(ActivityStatusCode.Error, "User or Request identifier not found in context.");
            throw new InvalidOperationException("User or Request identifier could not be determined for rate limiting.");
        }

        // Add identifiers to the activity trace for correlation.
        activity?.SetTag("ratelimit.user_id", rateLimitedContext.UserId);
        activity?.SetTag("ratelimit.request_id", rateLimitedContext.RequestId);

        logger.LogInformation("Retrieved user identifier {Identifier} for request {RequestId}.",
            rateLimitedContext.UserId,
            rateLimitedContext.RequestId);

        //Apply rate limiting based on the user identifier
        var commandName = request.GetType().Name;
        var isAllowed = rateLimiter.AllowRequest(
            rateLimitedContext.UserId,
            commandName
        );
        if (!isAllowed)
        {
            logger.LogWarning("Rate limit exceeded for user {Identifier} on request {RequestId} of type {RequestType}.",
                rateLimitedContext.UserId, rateLimitedContext.RequestId, request.GetType().Name);

            // Record that the request was throttled and mark the activity as failed.
            activity?.AddEvent(new ActivityEvent("RequestThrottled"));
            activity?.SetStatus(ActivityStatusCode.Error, "Rate limit exceeded.");
            throw new RateLimitExceededException(rateLimitedContext, "Rate limit exceeded for user.");
        }

        logger.LogInformation("Request {RequestId} for user {Identifier} passed rate limiting check.",
            rateLimitedContext.RequestId, rateLimitedContext.UserId);

        // Mark the activity as successful before proceeding.
        activity?.SetStatus(ActivityStatusCode.Ok);
        return await next(cancellationToken).ConfigureAwait(false);
    }
}
