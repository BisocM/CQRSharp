namespace CQRSharp.Persistence;

/// <summary>
///     The state of an outbox at one instant, as <see cref="IOutboxStore.GetBacklogAsync" /> measures it: what the
///     outbox gauges publish and the outbox health check judges.
/// </summary>
/// <param name="PendingCount">
///     Messages still to be delivered: pending (due or backing off) or in progress. A scheduled message whose due time
///     has not come is not counted here but in <see cref="ScheduledCount" />.
/// </param>
/// <param name="DeadLetterCount">Messages that were dead-lettered and not yet requeued or purged.</param>
/// <param name="OldestPendingCreatedAt">
///     The <see cref="OutboxMessage.CreatedAt" /> of the oldest undelivered message, or null when nothing is pending. The
///     distance from now is the outbox's lag. A scheduled message counts from its due time, once that has come.
/// </param>
public readonly record struct OutboxBacklog(long PendingCount, long DeadLetterCount, DateTime? OldestPendingCreatedAt)
{
    /// <summary>
    ///     Messages scheduled for later delivery whose due time has not come yet (see <see cref="ISchedulingOutboxStore" />):
    ///     not late, so neither in <see cref="PendingCount" /> nor in the lag. Always zero for a store that cannot schedule.
    /// </summary>
    public long ScheduledCount { get; init; }

    /// <summary>An empty outbox: nothing pending, nothing dead-lettered.</summary>
    public static OutboxBacklog Empty { get; } = new(0, 0, null);

    /// <summary>How far behind the outbox is: the age of the oldest undelivered message at <paramref name="utcNow" />, or zero when nothing is pending.</summary>
    /// <param name="utcNow">The current UTC time.</param>
    /// <returns>The lag; never negative.</returns>
    public TimeSpan LagAt(DateTime utcNow)
    {
        if (OldestPendingCreatedAt is not { } oldest) return TimeSpan.Zero;
        var lag = utcNow - oldest;
        return lag < TimeSpan.Zero ? TimeSpan.Zero : lag;
    }
}
