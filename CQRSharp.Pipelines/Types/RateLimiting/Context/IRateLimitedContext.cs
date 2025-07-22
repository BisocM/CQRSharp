using CQRSharp.Abstractions.Data.Interfaces.Context;

namespace CQRSharp.Pipelines.Types.RateLimiting.Context;

/// <summary>
///     A mandatory interface to be implemented on all contexts where you intend to use the built-in rate limiting
///     behaviour.
/// </summary>
public interface IRateLimitedContext : IRequestContext
{
    /// <summary>
    ///     The ID of the request. May be custom-defined by the user in their respective context factory.
    /// </summary>
    public object RequestId { get; set; }

    /// <summary>
    ///     The ID of the user to whom the request belongs to. May be custom-defined by the user in their respective context
    ///     factory.
    /// </summary>
    public object UserId { get; set; }
}