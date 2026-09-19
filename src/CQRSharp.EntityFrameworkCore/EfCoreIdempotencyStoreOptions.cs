namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     Tuning knobs for <see cref="EfCoreIdempotencyStore{TContext}" />.
/// </summary>
public sealed class EfCoreIdempotencyStoreOptions
{
    /// <summary>
    ///     How long a claimed key is remembered before it expires, which is exactly the deduplication window: a request
    ///     whose key was claimed longer ago than this is no longer treated as a duplicate. Expiry also lets a crashed
    ///     claimant's key self-heal. It does NOT bound the table: an expired row is reused only when the same key
    ///     returns and is never deleted, so purge expired rows on your own schedule. Must be greater than zero.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);
}
