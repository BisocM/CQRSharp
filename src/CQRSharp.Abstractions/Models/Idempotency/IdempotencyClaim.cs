namespace CQRSharp.Pipelines;

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
    Completed = 2
}

/// <summary>The outcome of claiming an idempotency key.</summary>
/// <param name="Status">Whether the key was claimed, is in use, or belongs to a completed request.</param>
/// <param name="StoredResult">
///     For <see cref="IdempotencyClaimStatus.Completed" />, the payload passed to
///     <see cref="IIdempotencyStore.CompleteAsync" /> — <c>null</c> when the completed request stored none.
/// </param>
public readonly record struct IdempotencyClaim(IdempotencyClaimStatus Status, byte[]? StoredResult = null)
{
    /// <summary>The key now belongs to the caller.</summary>
    public static IdempotencyClaim Claimed { get; } = new(IdempotencyClaimStatus.Claimed);

    /// <summary>The key is held by a request that has not completed.</summary>
    public static IdempotencyClaim InProgress { get; } = new(IdempotencyClaimStatus.InProgress);

    /// <summary>Creates the result for a key whose request already completed.</summary>
    /// <param name="storedResult">The payload stored at completion, if any.</param>
    public static IdempotencyClaim Completed(byte[]? storedResult) => new(IdempotencyClaimStatus.Completed, storedResult);

    /// <summary><c>true</c> when the caller won the key and should run the request.</summary>
    public bool IsClaimed => Status == IdempotencyClaimStatus.Claimed;
}
