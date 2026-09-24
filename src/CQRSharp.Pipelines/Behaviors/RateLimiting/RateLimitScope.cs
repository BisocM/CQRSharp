namespace CQRSharp.Pipelines;

/// <summary>
///     Which requests of one caller share a token bucket.
/// </summary>
public enum RateLimitScope
{
    /// <summary>
    ///     One bucket per caller (<see cref="IRateLimitedContext.UserId" />), shared by every rate-limited request type.
    /// </summary>
    Global,

    /// <summary>
    ///     One bucket per caller and request type: exhausting one type's budget leaves the caller's other request types
    ///     (commands, queries and streams alike) unaffected.
    /// </summary>
    PerRequestType
}
