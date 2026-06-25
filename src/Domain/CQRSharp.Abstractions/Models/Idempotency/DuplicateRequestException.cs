namespace CQRSharp.Abstractions.Models.Idempotency;

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
        : base($"A request with idempotency key '{idempotencyKey}' has already been processed.")
    {
        IdempotencyKey = idempotencyKey;
    }

    /// <summary>Gets the idempotency key that identified the duplicate request.</summary>
    public string IdempotencyKey { get; }
}