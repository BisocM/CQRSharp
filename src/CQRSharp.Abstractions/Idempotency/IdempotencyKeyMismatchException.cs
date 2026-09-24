namespace CQRSharp;

/// <summary>
///     Thrown by the idempotency behavior when a request reuses an idempotency key that was already used by a request
///     with a <em>different</em> payload. The original is neither replayed nor re-run: the client sent two different
///     requests under one key, which is a client error (an HTTP API answers <c>422 Unprocessable Content</c>).
/// </summary>
public sealed class IdempotencyKeyMismatchException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="IdempotencyKeyMismatchException" /> class.</summary>
    /// <param name="idempotencyKey">The reused key.</param>
    public IdempotencyKeyMismatchException(string idempotencyKey)
        : base($"The idempotency key '{idempotencyKey}' was already used by a request with a different payload.")
        => IdempotencyKey = idempotencyKey;

    /// <summary>The idempotency key that was reused.</summary>
    public string IdempotencyKey { get; }
}
