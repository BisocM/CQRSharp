namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     Tuning knobs for the EF Core idempotency store (<c>AddEntityFrameworkCoreIdempotencyStore&lt;TContext&gt;()</c>, or
///     <c>UseIdempotency(i =&gt; i.UseEntityFrameworkCore&lt;TContext&gt;())</c>) and its retention.
/// </summary>
public sealed class EfCoreIdempotencyStoreOptions
{
    /// <summary>
    ///     How long a claimed key is remembered before it expires, which is exactly the deduplication window: a request
    ///     whose key was claimed longer ago than this is no longer treated as a duplicate. Expiry also lets a crashed
    ///     claimant's key self-heal. Must be greater than zero. Default: 24 hours.
    /// </summary>
    /// <remarks>
    ///     Expired keys are deleted by a hosted service registered with the store - when the host starts, then once per
    ///     retention window, at least hourly - in bounded pages, never on a request's claim.
    /// </remarks>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);
}
