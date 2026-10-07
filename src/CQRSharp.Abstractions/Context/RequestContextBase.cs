namespace CQRSharp;

/// <summary>
///     The default request context: it carries only <see cref="CreatedAt" />. Derive from it to add what a request needs
///     (a user, a tenant) and keep the dispatcher's timestamping, or implement <see cref="IRequestContext" /> directly.
/// </summary>
public class RequestContextBase : IRequestContext
{
    // The application's clock while the dispatcher calls a context factory on this thread, null otherwise. A context the
    // factory builds with the parameterless constructor reads it rather than the system clock: when the factory completes
    // synchronously, as most do, that reading is the stamp, and the dispatcher reads no clock of its own. A clock read is
    // most of what building a context costs.
    [ThreadStatic] private static ApplicationClock? t_dispatchClock;

    /// <summary>
    ///     Creates a context without an explicit timestamp. It reads the system clock for now, or, while the dispatcher is
    ///     calling the context factory that builds it, the application's <c>TimeProvider</c>; when the dispatcher receives
    ///     it from a factory, it stamps it from the application's clock (deterministic under a fake one).
    ///     <see cref="RequestContextBase(DateTime)" /> stamps an explicit time, which the dispatcher leaves alone.
    /// </summary>
    public RequestContextBase()
    {
        _createdAt = t_dispatchClock is { } clock
            ? DateTime.SpecifyKind(clock.UtcNow, DateTimeKind.Local)
            : DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
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

    // One field holds the UTC timestamp and, in its Kind, whether the dispatcher still has to stamp it: Utc once it is
    // final; Unspecified for the provisional system-clock reading of a context built without an explicit time; Local for
    // the provisional reading of the application's clock that one built inside the dispatcher's call of its factory took,
    // which the stamp keeps when the factory completed synchronously. A separate flag would grow every context by a word,
    // and a context is allocated for every request.
    private DateTime _createdAt;

    /// <summary>
    ///     When the request was sent, in UTC: the explicit time the context was built with, or else the dispatcher's
    ///     reading of the application's <c>TimeProvider</c>.
    /// </summary>
    public DateTime CreatedAt => DateTime.SpecifyKind(_createdAt, DateTimeKind.Utc);

    // True until the dispatcher stamps a context built without an explicit timestamp.
    internal bool IsTimestampPending => _createdAt.Kind != DateTimeKind.Utc;

    /// <summary>
    ///     Stamps a context built without an explicit timestamp, once, with the time it took from the application's clock
    ///     while the dispatcher called its factory: the stamp for a factory that completed synchronously. Returns
    ///     <see langword="false" /> for a context that took no such time (built with the system clock), which is left for
    ///     <see cref="StampFromApplicationClock" />.
    /// </summary>
    internal bool TryStampWithDispatchReading()
    {
        if (_createdAt.Kind != DateTimeKind.Local) return false;
        _createdAt = DateTime.SpecifyKind(_createdAt, DateTimeKind.Utc);
        return true;
    }

    /// <summary>Stamps a context built without an explicit timestamp from the application's clock; once, and only such a one.</summary>
    internal void StampFromApplicationClock(DateTime utcNow)
    {
        if (IsTimestampPending) _createdAt = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
    }

    /// <summary>
    ///     Starts the synchronous part of the dispatcher's call of a context factory: until <see cref="ExitDispatch" />, a
    ///     context built on this thread with the parameterless constructor reads <paramref name="clock" />.
    /// </summary>
    /// <param name="clock">The application's clock.</param>
    /// <returns>The clock this one replaces (a factory can dispatch too), for <see cref="ExitDispatch" />.</returns>
    internal static ApplicationClock? EnterDispatch(ApplicationClock clock)
    {
        var outer = t_dispatchClock;
        t_dispatchClock = clock;
        return outer;
    }

    /// <summary>Ends what <see cref="EnterDispatch" /> started, restoring the clock it returned.</summary>
    internal static void ExitDispatch(ApplicationClock? outer) => t_dispatchClock = outer;
}
