namespace CQRSharp.Redis.Idempotency;

/// <summary>
///     Configuration for the Redis-backed durable idempotency store.
/// </summary>
public sealed class RedisIdempotencyOptions
{
    /// <summary>
    ///     Namespace prefix applied to every Redis key the store uses (one key per idempotency key). Isolate
    ///     independent stores by giving them different prefixes; must be non-empty.
    /// </summary>
    public string KeyPrefix { get; set; } = "cqrs:idemp:";

    /// <summary>
    ///     How long a claimed key is remembered before Redis expires it, which is exactly the deduplication window:
    ///     a request whose key was claimed longer ago than this is no longer treated as a duplicate. The expiry is
    ///     server-timed by Redis (no client clock is consulted). Must be greater than zero.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    ///     The Redis logical database index to use; -1 selects the connection's default database.
    /// </summary>
    public int Database { get; set; } = -1;
}
