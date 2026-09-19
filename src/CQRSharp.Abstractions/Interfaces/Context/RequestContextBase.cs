namespace CQRSharp;

/// <summary>
///     Default implementation of IRequestContext. Can be used as a fallback or baseline.
///     Users can extend this class or implement IRequestContext directly.
/// </summary>
public class RequestContextBase : IRequestContext
{
    /// <summary>
    ///     Creates a context stamped with the system clock. Prefer <see cref="RequestContextBase(DateTime)" /> from a
    ///     context factory, passing <c>TimeProvider.GetUtcNow()</c>, so the timestamp follows the application's clock
    ///     (and is deterministic under a fake one). The built-in default factory does exactly that.
    /// </summary>
    public RequestContextBase() : this(DateTime.UtcNow)
    {
    }

    /// <summary>Creates a context stamped with <paramref name="createdAt" />.</summary>
    /// <param name="createdAt">The UTC time the context was created, normally read from the injected <c>TimeProvider</c>.</param>
    public RequestContextBase(DateTime createdAt)
    {
        CreatedAt = createdAt;
    }

    /// <summary>
    ///     The time at which the request context was created.
    /// </summary>
    public DateTime CreatedAt { get; }
}