namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     Tracks when the EF Core outbox store last purged processed messages. The store is scoped — the processor creates
///     one per poll — so this lives in a singleton: without it every poll would issue a purge.
/// </summary>
public sealed class EfCoreOutboxPurgeSchedule
{
    private long _nextPurgeTicks;

    /// <summary>
    ///     Returns <c>true</c> to at most one caller per <paramref name="interval" />, and schedules the next purge.
    /// </summary>
    /// <param name="now">The current UTC time, from the store's <see cref="TimeProvider" />.</param>
    /// <param name="interval">The minimum time between two purges.</param>
    public bool TryBegin(DateTime now, TimeSpan interval)
    {
        var due = Interlocked.Read(ref _nextPurgeTicks);
        return now.Ticks >= due &&
               Interlocked.CompareExchange(ref _nextPurgeTicks, (now + interval).Ticks, due) == due;
    }
}
