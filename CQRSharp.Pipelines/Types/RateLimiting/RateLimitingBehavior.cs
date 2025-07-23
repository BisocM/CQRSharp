using System.Diagnostics;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines.Types.RateLimiting.Context;
using CQRSharp.Abstractions.Data.Attributes.Pipelines;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Pipelines.Telemetry;
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
[PipelinePriority(int.MinValue)]
public sealed class RateLimitingBehavior<TRequest, TResult>(
    ILogger<RateLimitingBehavior<TRequest, TResult>> logger,
    RateLimiter rateLimiter)
    : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
{
    /// <inheritdoc />
    public async Task<TResult> Handle(TRequest request,
        Func<CancellationToken, Task<TResult>> next, CancellationToken cancellationToken)
    {
        // Creates a trace activity for the rate limiting check.
        using var activity = PipelineTelemetry.StartActivity("RateLimiting.Check", request);

        //Ensure the request can be cast to RequestBase for identifier extraction
        if (request.Context is not IRateLimitedContext baseRequest)
        {
            logger.LogError(
                "Rate limiting failed: request must inherit from RequestBase<IRateLimitedContext> to support rate limiting.");
            // Mark the activity as failed due to a configuration error.
            activity?.SetStatus(ActivityStatusCode.Error, "Invalid request context for rate limiting.");
            throw new InvalidOperationException(
                "Request must inherit from RequestBase<IRateLimitedContext> to support rate limiting.");
        }

        if (baseRequest.UserId == null || baseRequest.RequestId == null)
        {
            logger.LogWarning("User or Request identifier could not be determined for request.");
            // Mark the activity as failed due to missing identifiers.
            activity?.SetStatus(ActivityStatusCode.Error, "User or Request identifier not found in context.");
            throw new InvalidOperationException("User or Request identifier could not be determined for rate limiting.");
        }

        // Add identifiers to the activity trace for correlation.
        activity?.SetTag("ratelimit.user_id", baseRequest.UserId);
        activity?.SetTag("ratelimit.request_id", baseRequest.RequestId);

        logger.LogInformation("Retrieved user identifier {Identifier} for request {RequestId}.",
            baseRequest.UserId,
            baseRequest.RequestId);

        //Apply rate limiting based on the user identifier
        var commandName = request.GetType().Name;
        var isAllowed = rateLimiter.AllowRequest(
            (string)baseRequest.UserId,
            commandName
        );
        if (!isAllowed)
        {
            logger.LogWarning("Rate limit exceeded for user {Identifier} on request {RequestId} of type {RequestType}.",
                baseRequest.UserId, baseRequest.RequestId, baseRequest.GetType().Name);

            // Record that the request was throttled and mark the activity as failed.
            activity?.AddEvent(new ActivityEvent("RequestThrottled"));
            activity?.SetStatus(ActivityStatusCode.Error, "Rate limit exceeded.");
            throw new RateLimitExceededException(baseRequest, "Rate limit exceeded for user.");
        }

        logger.LogInformation("Request {RequestId} for user {Identifier} passed rate limiting check.",
            baseRequest.RequestId, baseRequest.UserId);

        // Mark the activity as successful before proceeding.
        activity?.SetStatus(ActivityStatusCode.Ok);
        return await next(cancellationToken);
    }
}