using CQRSharp.Interfaces.Markers.Request;

namespace CQRSharp.RateLimiting.Exceptions
{
    public sealed class RateLimitExceededException(RequestBase request, string message) : Exception($"Request {request.Id} triggered a rate limit exception. " + message);
}