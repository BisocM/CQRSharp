using CQRSharp.Pipelines.Types.RateLimiting.Context;

namespace CQRSharp.Sample.Application.Contexts;

public class SampleRequestContext(string requestId, string userId, DateTime createdAt)
    : IRateLimitedContext
{
    public DateTime CreatedAt { get; } = createdAt;
    public string RequestId { get; set; } = requestId;
    public string UserId { get; set; } = userId;
}
