using CQRSharp.Pipelines.Types.RateLimiting.Context;
using CQRSharp.Sample.Management.Menu;

namespace CQRSharp.Sample.Context;

public class SampleRequestContext(object requestId, object userId, DateTime createdAt)
    : IRateLimitedContext
{
    /// <summary>
    ///     The desired menu state after the completion of the command. Remains null if no menu state needed.
    /// </summary>
    public MenuState? NextState { get; set; }

    public DateTime CreatedAt { get; } = createdAt;

    public object RequestId { get; set; } = requestId;
    public object UserId { get; set; } = userId;
}