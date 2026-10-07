namespace CQRSharp;

/// <summary>
///     The application's clock as the dispatcher lends it to the context factory it is calling, so a context built there
///     with the parameterless <see cref="RequestContextBase" /> constructor reads the application's time once, instead of
///     reading the system clock and then being stamped again. Implemented in CQRSharp.Core over the application's
///     <c>TimeProvider</c>, which the contracts cannot reference on every target framework.
/// </summary>
internal abstract class ApplicationClock
{
    /// <summary>The current time, in UTC.</summary>
    public abstract DateTime UtcNow { get; }
}
