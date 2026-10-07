namespace CQRSharp;

/// <summary>
///     The application's <see cref="TimeProvider" /> as the <see cref="ApplicationClock" /> the dispatcher lends to the
///     context factories it calls (see <see cref="RequestContextBase" />). One per service provider.
/// </summary>
internal sealed class TimeProviderApplicationClock(TimeProvider timeProvider) : ApplicationClock
{
    public override DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;
}
