namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     Tuning knobs for <see cref="EfCoreIdempotencyStore{TContext}" />.
/// </summary>
public sealed class EfCoreIdempotencyStoreOptions
{
    /// <summary>
    ///     How long a claimed key is remembered before it expires, which is exactly the deduplication window: a request
    ///     whose key was claimed longer ago than this is no longer treated as a duplicate. Expiry also bounds the table
    ///     for keys that were never released and lets a crashed claimant's key self-heal. Must be greater than zero.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);
}
