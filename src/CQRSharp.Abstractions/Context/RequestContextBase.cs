namespace CQRSharp;

/// <summary>
///     The default request context: it carries only <see cref="CreatedAt" />. Derive from it to add what a request needs
///     (a user, a tenant) and keep the dispatcher's timestamping, or implement <see cref="IRequestContext" /> directly.
/// </summary>
public class RequestContextBase : IRequestContext
{
    /// <summary>
    ///     Creates a context without an explicit timestamp. It reads the system clock for now; when the dispatcher
    ///     receives it from a context factory, the dispatcher stamps it from the application's <c>TimeProvider</c>
    ///     (deterministic under a fake one). <see cref="RequestContextBase(DateTime)" /> stamps an explicit time, which
    ///     the dispatcher leaves alone.
    /// </summary>
    public RequestContextBase()
    {
        _createdAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
    }

    /// <summary>Creates a context stamped with <paramref name="createdAt" />.</summary>
    /// <param name="createdAt">The UTC time the context was created, normally read from the injected <c>TimeProvider</c>.</param>
    public RequestContextBase(DateTime createdAt)
    {
        // The property is UTC: an unspecified kind (a DateTimeOffset's .DateTime, say) is taken as UTC, a local time
        // is converted.
        _createdAt = createdAt.Kind == DateTimeKind.Local
            ? createdAt.ToUniversalTime()
            : DateTime.SpecifyKind(createdAt, DateTimeKind.Utc);
    }

    // One field holds the timestamp and, in its Kind, whether the dispatcher still has to stamp it: a context built
    // without an explicit time keeps its provisional reading as Unspecified until then, and every other value is Utc. A
    // separate flag would grow every context by a word, and a context is allocated for every request.
    private DateTime _createdAt;

    /// <summary>
    ///     When the request was sent, in UTC: the explicit time the context was built with, or else the dispatcher's
    ///     reading of the application's <c>TimeProvider</c>.
    /// </summary>
    public DateTime CreatedAt => DateTime.SpecifyKind(_createdAt, DateTimeKind.Utc);

    // True until the dispatcher stamps a context built without an explicit timestamp.
    internal bool IsTimestampPending => _createdAt.Kind == DateTimeKind.Unspecified;

    /// <summary>Stamps a context built without an explicit timestamp from the application's clock; once, and only such a one.</summary>
    internal void StampFromApplicationClock(DateTime utcNow)
    {
        if (IsTimestampPending) _createdAt = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
    }
}
