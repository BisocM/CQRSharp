namespace CQRSharp;

/// <summary>
///     Thrown by the rate-limiting behavior (<c>UseRateLimiting(...)</c>) when the caller's token bucket holds no token
///     for the request.
/// </summary>
/// <remarks>
///     A verdict, not a fault: the resilience behavior never retries it. <c>AddCqrsProblemDetails()</c> maps it to
///     <c>429 Too Many Requests</c> with a <c>Retry-After</c> header taken from <see cref="RetryAfter" />.
/// </remarks>
public sealed class RateLimitExceededException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message that describes the rejection.</param>
    /// <param name="retryAfter">How long until the caller's bucket holds a token again; <see langword="null" /> when unknown.</param>
    public RateLimitExceededException(string message, TimeSpan? retryAfter = null)
        : base(message)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>
    ///     How long until the caller's token bucket holds a token again, as computed when it rejected the request;
    ///     <see langword="null" /> when unknown. The rate-limiting behavior reports whole milliseconds, rounded up, so a
    ///     caller that waits exactly this long is never back before the token is there. Advisory: a concurrent request
    ///     from the same caller can take that token first, in which case the retry is rejected again with a fresh value.
    /// </summary>
    public TimeSpan? RetryAfter { get; }
}
