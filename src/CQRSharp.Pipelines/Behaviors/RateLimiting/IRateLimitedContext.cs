namespace CQRSharp.Pipelines;

/// <summary>
///     Implement this on a request's context to opt the request into the rate-limiting behavior
///     (<c>UseRateLimiting(...)</c>). The limiter keeps one token bucket per <see cref="UserId" /> (and, in
///     <see cref="RateLimitScope.PerRequestType" /> scope, per request type); a request whose context does not implement
///     this interface is not limited.
/// </summary>
public interface IRateLimitedContext : IRequestContext
{
    /// <summary>
    ///     The key the request is limited under: normally the authenticated user's id; for an anonymous caller another
    ///     stable key, such as the client address. It must come from a trusted source (a caller who can choose it can
    ///     sidestep their limit by varying it), and it must not be empty: the behavior throws
    ///     <see cref="InvalidOperationException" /> for a context that opts in without one.
    /// </summary>
    string UserId { get; }
}
