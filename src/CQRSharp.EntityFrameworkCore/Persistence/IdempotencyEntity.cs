namespace CQRSharp.EntityFrameworkCore.Persistence;

/// <summary>
///     The EF Core row that records a single claimed idempotency key. One row exists per live claim; the row is
///     deleted on release and taken over once it ages past its expiry. <see cref="ExpiresAt" /> is the UTC instant at
///     which the claim stops deduplicating — a later claim of the same key after that time wins, which both bounds the
///     table for keys that were never released and lets a crashed claimant's key self-heal.
/// </summary>
public sealed class IdempotencyEntity
{
    /// <summary>The idempotency key identifying the logical request (primary key, supplied by the caller, never database-generated).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    ///     The UTC instant at which this claim expires. While the current time is before this, the key is a live claim
    ///     and a repeat is rejected as a duplicate; at or after it the claim is aged out and may be taken over.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    ///     Optimistic-concurrency token. Configured as a manual row-version that the store increments when it takes
    ///     over an expired row; if two processes both read the same expired row and both try to save the take-over,
    ///     the second save sees a stale token and EF raises a concurrency exception, which is exactly how the take-over
    ///     is kept atomic so two claimants can never both revive the same expired key. (The INSERT of a fresh key is
    ///     instead made race-safe by the unique primary key, not this token.)
    /// </summary>
    public uint RowVersion { get; set; }
}
