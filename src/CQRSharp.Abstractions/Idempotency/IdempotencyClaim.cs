namespace CQRSharp.Persistence;

/// <summary>What <see cref="IIdempotencyStore.TryClaimAsync" /> found for an idempotency key.</summary>
public enum IdempotencyClaimStatus
{
    /// <summary>The key was free (or its previous claim had expired) and now belongs to the caller: run the request.</summary>
    Claimed = 0,

    /// <summary>
    ///     Another request with this key is still running — or crashed without releasing it and its claim has not expired
    ///     yet. There is no result to replay; the duplicate is rejected.
    /// </summary>
    InProgress = 1,

    /// <summary>A request with this key already completed. Its stored result, if any, can be replayed to the caller.</summary>
    Completed = 2,

    /// <summary>
    ///     The key is held or completed by a request with a <em>different</em> payload fingerprint: the client reused an
    ///     idempotency key for another request. Neither replayed nor run; the duplicate is rejected as a client error.
    /// </summary>
    PayloadMismatch = 3
}

/// <summary>
///     The outcome of claiming an idempotency key, as <see cref="IIdempotencyStore.TryClaimAsync" /> answers it. Built
///     only through its factories — <see cref="ClaimedWith" />, <see cref="InProgress" />, <see cref="PayloadMismatch" />
///     and <see cref="Completed" /> — so every instance is a valid answer: a claim the caller won always carries the
///     token it later completes or releases the key with.
/// </summary>
public sealed record IdempotencyClaim
{
    private IdempotencyClaim(IdempotencyClaimStatus status, byte[]? storedResult, string? token)
    {
        Status = status;
        StoredResult = storedResult;
        Token = token;
    }

    /// <summary>The key is held by a request that has not completed.</summary>
    public static IdempotencyClaim InProgress { get; } = new(IdempotencyClaimStatus.InProgress, null, null);

    /// <summary>The key was already used with a different request payload.</summary>
    public static IdempotencyClaim PayloadMismatch { get; } = new(IdempotencyClaimStatus.PayloadMismatch, null, null);

    /// <summary>Whether the key was claimed, is in use, or belongs to a completed request.</summary>
    public IdempotencyClaimStatus Status { get; }

    /// <summary>
    ///     For <see cref="IdempotencyClaimStatus.Completed" />, the payload passed to
    ///     <see cref="IIdempotencyStore.CompleteAsync" /> — <c>null</c> when the completed request stored none, and for
    ///     every other status.
    /// </summary>
    public byte[]? StoredResult { get; }

    /// <summary>
    ///     For <see cref="IdempotencyClaimStatus.Claimed" />, what identifies this very claim (never null or empty then):
    ///     the caller hands it back to <see cref="IIdempotencyStore.CompleteAsync" /> or
    ///     <see cref="IIdempotencyStore.ReleaseAsync" />, and a store acts only while the key still carries it. A claimant
    ///     that outlived the retention window, and whose key was taken over, therefore cannot complete or release its
    ///     successor's claim. <c>null</c> for every other status.
    /// </summary>
    public string? Token { get; }

    /// <summary><c>true</c> when the caller won the key and should run the request.</summary>
    public bool IsClaimed => Status == IdempotencyClaimStatus.Claimed;

    /// <summary>The key now belongs to the caller, identified by <paramref name="token" />.</summary>
    /// <param name="token">An opaque value unique to this claim (a store-generated id, a version stamp).</param>
    /// <exception cref="ArgumentException"><paramref name="token" /> is null or empty.</exception>
    public static IdempotencyClaim ClaimedWith(string token)
    {
        if (string.IsNullOrEmpty(token)) throw new ArgumentException("A claim needs a non-empty token.", nameof(token));
        return new IdempotencyClaim(IdempotencyClaimStatus.Claimed, null, token);
    }

    /// <summary>Creates the result for a key whose request already completed.</summary>
    /// <param name="storedResult">The payload stored at completion, if any.</param>
    public static IdempotencyClaim Completed(byte[]? storedResult) => new(IdempotencyClaimStatus.Completed, storedResult, null);
}
