namespace CQRSharp.Abstractions.Interfaces.Idempotency;

/// <summary>
///     Persistence contract for request idempotency. Implement this (e.g. backed by a database table, Redis, etc.) and
///     register it so the idempotency behavior can detect duplicate requests.
/// </summary>
/// <remarks>
///     Claiming and releasing must be atomic with respect to concurrent callers using the same key, so that exactly
///     one caller observes a successful claim. For durable at-most-once semantics across process restarts the store
///     must be persistent; an in-memory implementation only deduplicates within a single process lifetime.
///     <para>
///         Keys are compared with ordinal, case-sensitive equality: two keys that differ only by case are distinct
///         requests. Implementations must honor that — for example a relational backend whose column collation folds
///         case (such as SQL Server's default) needs a binary/case-sensitive collation on the key column to match this
///         contract, otherwise it will wrongly reject a case-variant request as a duplicate.
///     </para>
///     <para>
///         Keys should stay within a bounded length (at most 512 characters) so they are portable across backends:
///         relational stores cap the key column to keep it indexable, so an over-long key that works in-memory or in
///         Redis can throw on a relational store. A caller with longer natural keys should hash them down first.
///     </para>
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>
    ///     Atomically attempts to claim <paramref name="key" /> for processing.
    /// </summary>
    /// <param name="key">The idempotency key identifying the logical request.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    ///     <c>true</c> if the key was newly claimed and the caller should proceed; <c>false</c> if the key was already
    ///     claimed (the request is a duplicate).
    /// </returns>
    Task<bool> TryClaimAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    ///     Releases a previously-claimed <paramref name="key" /> so the request can be processed again — used when a
    ///     claimed request fails before completing, so a later attempt (or retry) is not rejected as a duplicate.
    /// </summary>
    /// <param name="key">The idempotency key to release.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task ReleaseAsync(string key, CancellationToken cancellationToken);
}