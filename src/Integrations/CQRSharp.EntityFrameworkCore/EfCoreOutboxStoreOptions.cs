namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     Tuning knobs for <see cref="EfCoreOutboxStore{TContext}" />.
/// </summary>
public sealed class EfCoreOutboxStoreOptions
{
    /// <summary>
    ///     How long a claimed (in-progress) message stays leased before it is considered abandoned and may be
    ///     reclaimed by another processor. Set this comfortably longer than a normal dispatch takes so a healthy
    ///     processor is never raced; too short risks double-dispatch, too long delays recovery after a crash.
    /// </summary>
    public TimeSpan VisibilityTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     The maximum number of optimistic-concurrency retries when a claim or mutation loses the row-version race
    ///     against a competing processor before giving up on that row for the current call.
    /// </summary>
    public int MaxClaimAttempts { get; set; } = 3;
}
