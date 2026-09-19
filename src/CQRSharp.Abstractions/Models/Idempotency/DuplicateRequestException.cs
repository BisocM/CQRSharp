namespace CQRSharp;

/// <summary>
///     Thrown by the idempotency behavior when a request's idempotency key has already been claimed — i.e. the request
///     is a duplicate of one that is in flight or already processed.
/// </summary>
public sealed class DuplicateRequestException : Exception
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="DuplicateRequestException" /> class.
    /// </summary>
    /// <param name="idempotencyKey">The idempotency key that was already claimed.</param>
    public DuplicateRequestException(string idempotencyKey)
        : this(idempotencyKey, false)
    {
    }

    /// <summary>Creates the exception, saying whether the original request is still running.</summary>
    /// <param name="idempotencyKey">The idempotency key of the rejected duplicate.</param>
    /// <param name="isInProgress">
    ///     <c>true</c> when the request that owns the key has not completed yet (the caller can retry shortly);
    ///     <c>false</c> when it completed but its result could not be replayed.
    /// </param>
    public DuplicateRequestException(string idempotencyKey, bool isInProgress)
        : base(isInProgress
            ? $"A request with idempotency key '{idempotencyKey}' is still being processed."
            : $"A request with idempotency key '{idempotencyKey}' has already been processed.")
    {
        IdempotencyKey = idempotencyKey;
        IsInProgress = isInProgress;
    }

    /// <summary>
    ///     Whether the request that owns the key is still running (as opposed to completed with a result that could not be
    ///     replayed). An HTTP API would typically answer 409 with a Retry-After for the former.
    /// </summary>
    public bool IsInProgress { get; }

    /// <summary>Gets the idempotency key that identified the duplicate request.</summary>
    public string IdempotencyKey { get; }
}