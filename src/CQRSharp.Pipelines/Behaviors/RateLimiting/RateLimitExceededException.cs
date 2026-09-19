namespace CQRSharp.Pipelines;

/// <inheritdoc />
public sealed class RateLimitExceededException(IRateLimitedContext requestContext, string message) : Exception(
    $"Request {requestContext.RequestId} from user {requestContext.UserId} triggered a rate limit exception. " +
    message);