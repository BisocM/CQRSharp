namespace CQRSharp.Core.Options;

/// <summary>
///     Options for the in-process in-memory idempotency store.
/// </summary>
public sealed class InMemoryIdempotencyStoreOptions
{
    /// <summary>
    ///     How long a claimed idempotency key is remembered. Within this window a repeat of the same key is rejected as
    ///     a duplicate; once it elapses the key is forgotten so the store does not grow without bound for keys that were
    ///     never released (i.e. requests that completed successfully). Must be greater than zero.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);
}
