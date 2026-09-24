using CQRSharp.Pipelines;

namespace CQRSharp;

/// <summary>
///     Thrown by the timeout behavior (<c>UseTimeout(...)</c>) when a request's handler, or a stream's whole
///     enumeration, does not finish within <see cref="TimeoutOptions.Timeout" />.
/// </summary>
/// <remarks>
///     A <see cref="TimeoutException" />, so existing <c>catch (TimeoutException)</c> code keeps working, but a distinct
///     type, so it can be told apart from the timeouts a dependency throws (a database driver, an HTTP or Redis client):
///     the resilience behavior never retries it, while it retries those; and <c>AddCqrsProblemDetails()</c> maps only it
///     to <c>504</c>. The timeout is cooperative: it cancels the token the handler receives, and this exception is thrown
///     once the handler observes that cancellation.
/// </remarks>
public sealed class RequestTimeoutException : TimeoutException
{
    /// <summary>Creates the exception for a request of <paramref name="requestType" /> that exceeded <paramref name="timeout" />.</summary>
    /// <param name="requestType">The type of the request that timed out.</param>
    /// <param name="timeout">The time budget the request exceeded.</param>
    /// <param name="innerException">The cancellation the handler observed, if any.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="requestType" /> is <see langword="null" />.</exception>
    public RequestTimeoutException(Type requestType, TimeSpan timeout, Exception? innerException = null)
        : base(
            $"{(requestType ?? throw new ArgumentNullException(nameof(requestType))).Name} did not complete within its timeout of {timeout}.",
            innerException)
    {
        RequestType = requestType;
        Timeout = timeout;
    }

    /// <summary>The type of the request that timed out.</summary>
    public Type RequestType { get; }

    /// <summary>The time budget the request exceeded (<see cref="TimeoutOptions.Timeout" />).</summary>
    public TimeSpan Timeout { get; }
}
