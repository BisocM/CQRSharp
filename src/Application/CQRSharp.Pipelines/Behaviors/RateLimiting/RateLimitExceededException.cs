using CQRSharp.Pipelines;

namespace CQRSharp.Pipelines.Behaviors.RateLimiting;

/// <inheritdoc />
public sealed class RateLimitExceededException(IRateLimitedContext requestContext, string message) : Exception(
    $"Request {requestContext.RequestId} from user {requestContext.UserId} triggered a rate limit exception. " +
    message);