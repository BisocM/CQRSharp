using CQRSharp.Core.Pipelines.Types.RateLimiting.Context;
using CQRSharp.Interfaces.Markers.Request;

namespace CQRSharp.Core.Pipelines.Types.RateLimiting;

/// <inheritdoc />
public sealed class RateLimitExceededException(RequestBase<IRateLimitedContext> request, string message) : Exception(
    $"Request {request.Context?.RequestId} from user {request.Context?.UserId} triggered a rate limit exception. " +
    message);