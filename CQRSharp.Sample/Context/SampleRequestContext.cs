using CQRSharp.Pipelines.Types.RateLimiting.Context;

namespace CQRSharp.Sample.Context;

public class SampleRequestContext(object requestId, object userId, DateTime createdAt)
    : IRateLimitedContext
{
    public DateTime CreatedAt { get; } = createdAt;
    public object RequestId { get; set; } = requestId;
    public object UserId { get; set; } = userId;
}