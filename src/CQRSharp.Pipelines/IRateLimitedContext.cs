using CQRSharp.Abstractions.Interfaces.Context;

namespace CQRSharp.Pipelines;

/// <summary>
///     Implement this on a request's context to opt the request into the built-in rate-limiting behavior. The limiter
///     keys buckets by <see cref="UserId" /> (and, in per-command scope, the request type).
/// </summary>
public interface IRateLimitedContext : IRequestContext
{
    /// <summary>
    ///     The ID of the request. May be custom-defined by the user in their respective context factory.
    /// </summary>
    public string RequestId { get; set; }

    /// <summary>
    ///     The ID of the user to whom the request belongs. May be custom-defined by the user in their respective
    ///     context factory.
    /// </summary>
    public string UserId { get; set; }
}
