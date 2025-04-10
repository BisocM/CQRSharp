using CQRSharp.Core.Pipelines.Types.RateLimiting.Context;
using CQRSharp.Shared.Data.Interfaces.Markers.Request;

namespace CQRSharp.Core.Pipelines.Types.RateLimiting;

/// <inheritdoc />
public sealed class RateLimitExceededException(IRateLimitedContext requestContext, string message) : Exception(
    $"Request {requestContext.RequestId} from user {requestContext.UserId} triggered a rate limit exception. " +
    message);