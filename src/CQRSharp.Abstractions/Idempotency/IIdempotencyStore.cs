namespace CQRSharp.Persistence;

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
///         Keys should stay within a bounded length (at most 450 characters) so they are portable across backends:
///         relational stores cap the key column to keep it indexable, so an over-long key that works in-memory or in
///         Redis is rejected by a relational store (the EF Core store refuses it before touching the database). A
///         caller with longer natural keys should hash them down first.
///     </para>
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>
    ///     Atomically attempts to claim <paramref name="key" /> for processing.
    /// </summary>
    /// <param name="key">The idempotency key identifying the logical request.</param>
    /// <param name="fingerprint">
    ///     A digest of the request's payload, or <c>null</c> when none is known. A store remembers the fingerprint a key
    ///     was claimed with and, when a later claim of the same key carries a <em>different</em> non-null fingerprint,
    ///     answers <see cref="IdempotencyClaimStatus.PayloadMismatch" /> instead of replaying or rejecting: the client
    ///     reused a key for a different request. A <c>null</c> on either side disables the comparison. The idempotency
    ///     behavior always passes a SHA-256 digest as 64 hexadecimal characters, so a store that sizes a column for it
    ///     needs no more than that.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    ///     Never <c>null</c>; built with the <see cref="IdempotencyClaim" /> factories.
    ///     <see cref="IdempotencyClaimStatus.Claimed" /> (<see cref="IdempotencyClaim.ClaimedWith" />, with a token unique
    ///     to this claim) if the key was newly claimed and the caller should proceed;
    ///     <see cref="IdempotencyClaimStatus.InProgress" /> if another request holds it and has not completed;
    ///     <see cref="IdempotencyClaimStatus.Completed" />, with whatever result was stored, if a request with this key
    ///     already completed; <see cref="IdempotencyClaimStatus.PayloadMismatch" /> if the key is held or completed under
    ///     a different fingerprint.
    /// </returns>
    Task<IdempotencyClaim> TryClaimAsync(string key, string? fingerprint, CancellationToken cancellationToken);

    /// <summary>
    ///     Marks a key this caller claimed as <b>completed</b> and stores the request's result, so a later duplicate can
    ///     be answered with the original outcome instead of being run again or rejected. The key keeps its original
    ///     expiry: the retention window is how long a duplicate is recognised, not how long after completion.
    /// </summary>
    /// <remarks>
    ///     Like <see cref="ReleaseAsync" />, this only affects the claim <paramref name="claimToken" /> identifies: when the
    ///     key was taken over since (the claim outlived the retention window), it changes nothing.
    /// </remarks>
    /// <param name="key">The idempotency key to complete.</param>
    /// <param name="claimToken">The <see cref="IdempotencyClaim.Token" /> of the caller's claim.</param>
    /// <param name="result">The serialized result to replay to duplicates, or <c>null</c> when there is nothing to store.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task CompleteAsync(string key, string claimToken, byte[]? result, CancellationToken cancellationToken);

    /// <summary>
    ///     Releases a previously-claimed <paramref name="key" /> so the request can be processed again — used when a
    ///     claimed request fails before completing, so a later attempt (or retry) is not rejected as a duplicate.
    /// </summary>
    /// <remarks>Only the claim <paramref name="claimToken" /> identifies is released; a successor's claim is left alone.</remarks>
    /// <param name="key">The idempotency key to release.</param>
    /// <param name="claimToken">The <see cref="IdempotencyClaim.Token" /> of the caller's claim.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task ReleaseAsync(string key, string claimToken, CancellationToken cancellationToken);
}
