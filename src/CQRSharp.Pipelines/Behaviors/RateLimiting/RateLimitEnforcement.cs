using System.Diagnostics;
using CQRSharp.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Pipelines;

/// <summary>The rate-limit check the request and streaming behaviors share.</summary>
internal static partial class RateLimitEnforcement
{
    /// <summary>
    ///     Takes a token for the request when its context opts in through <see cref="IRateLimitedContext" />, and throws
    ///     <see cref="RateLimitExceededException" /> when there is none. Its span covers the check alone, so it ends before
    ///     the rest of the pipeline runs and never becomes the parent of the handler's spans.
    /// </summary>
    /// <exception cref="RateLimitExceededException">Thrown when the caller is over the limit.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the context carries no <see cref="IRateLimitedContext.UserId" />.</exception>
    public static void Enforce<TRequest>(TRequest request, RequestRateLimiter rateLimiter, ILogger logger)
        where TRequest : IRequest
    {
        if (request.Context is not IRateLimitedContext context) return;

        using var activity = PipelineTelemetry.StartActivity<TRequest>("RateLimiting.Check");

        var userId = context.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            activity?.SetStatus(ActivityStatusCode.Error, "The IRateLimitedContext carries no UserId.");
            throw new InvalidOperationException(
                $"The context of {typeof(TRequest).Name} implements IRateLimitedContext but its UserId is empty, so the " +
                "request cannot be rate limited. Have the context factory set a stable, trusted key: the authenticated " +
                "user's id, or for an anonymous caller another key such as the client address.");
        }

        activity?.SetTag(CqrsTelemetry.Tags.RateLimitUserId, userId);

        if (rateLimiter.TryAcquire(userId, request.GetType(), out var retryAfter))
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
            return;
        }

        activity?.SetTag(CqrsTelemetry.Tags.RateLimitRetryAfterMilliseconds, retryAfter.TotalMilliseconds);
        activity?.SetStatus(ActivityStatusCode.Error, "Rate limit exceeded.");
        LogRejected(logger, typeof(TRequest).Name, userId, retryAfter.TotalMilliseconds);
        throw new RateLimitExceededException($"The rate limit for {typeof(TRequest).Name} was exceeded.", retryAfter);
    }

    // Information, not Warning: rejecting a caller over its limit is the limiter doing its job, not something an
    // operator has to act on.
    [LoggerMessage(4500, LogLevel.Information, "Rate limited {RequestName} for {UserId}; a token is available again in {RetryAfterMs}ms")]
    private static partial void LogRejected(ILogger logger, string requestName, string userId, double retryAfterMs);
}
