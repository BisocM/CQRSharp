using System.Diagnostics;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines.Behaviors.RateLimiting.Context;
using CQRSharp.Pipelines.Telemetry;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Pipelines.Behaviors.RateLimiting;

/// <summary>
///     Enforces rate limiting for streaming requests when the request context implements <see cref="IRateLimitedContext" />.
/// </summary>
public sealed class StreamRateLimitingBehavior<TRequest, TItem>(
    ILogger<StreamRateLimitingBehavior<TRequest, TItem>> logger,
    RateLimiter rateLimiter)
    : IStreamPipelineBehavior<TRequest, TItem>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    public int PipelineExecutionPriority => int.MinValue + 1;

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
            using var activity = PipelineTelemetry.StartActivity("RateLimiting.Check", request);

            if (request.Context is not IRateLimitedContext rateLimitedContext)
            {
                activity?.SetStatus(ActivityStatusCode.Ok, "Rate limiting skipped (no IRateLimitedContext).");
                await foreach (var item in next(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                    yield return item;
                yield break;
            }

            if (string.IsNullOrWhiteSpace(rateLimitedContext.UserId) ||
                string.IsNullOrWhiteSpace(rateLimitedContext.RequestId))
            {
                logger.LogError("Rate limiting failed: UserId/RequestId missing from IRateLimitedContext.");
                activity?.SetStatus(ActivityStatusCode.Error, "User or Request identifier not found in context.");
                throw new InvalidOperationException("User or Request identifier could not be determined for rate limiting.");
            }

            activity?.SetTag("ratelimit.user_id", rateLimitedContext.UserId);
            activity?.SetTag("ratelimit.request_id", rateLimitedContext.RequestId);

            logger.LogInformation("Retrieved user identifier {Identifier} for request {RequestId}.",
                rateLimitedContext.UserId,
                rateLimitedContext.RequestId);

            var isAllowed = rateLimiter.AllowRequest(rateLimitedContext.UserId, request.GetType());
            if (!isAllowed)
            {
                logger.LogWarning("Rate limit exceeded for user {Identifier} on request {RequestId} of type {RequestType}.",
                    rateLimitedContext.UserId, rateLimitedContext.RequestId, request.GetType().Name);

                activity?.AddEvent(new ActivityEvent("RequestThrottled"));
                activity?.SetStatus(ActivityStatusCode.Error, "Rate limit exceeded.");
                throw new RateLimitExceededException(rateLimitedContext, "Rate limit exceeded for user.");
            }

            activity?.SetStatus(ActivityStatusCode.Ok);

            await foreach (var item in next(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
    }
}