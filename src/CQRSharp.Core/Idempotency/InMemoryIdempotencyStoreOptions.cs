namespace CQRSharp;

/// <summary>
///     Options for the in-process in-memory idempotency store.
/// </summary>
public sealed class InMemoryIdempotencyStoreOptions
{
    /// <summary>
    ///     How long a claimed idempotency key is remembered, which is the deduplication window: within it a repeat of the
    ///     same key is treated as a duplicate (answered with the stored result, or rejected); once it elapses the key is
    ///     forgotten, so the store does not grow without bound. Must be greater than zero and at most 10 years. Default:
    ///     24 hours.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);
}
