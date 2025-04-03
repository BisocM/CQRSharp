using CQRSharp.Interfaces.Context;

namespace CQRSharp.Core.Pipelines.Types.RateLimiting.Context;

public interface IRateLimitedContext : IRequestContext
{
    public object RequestId { get; set; }
    public object UserId { get; set; }
}