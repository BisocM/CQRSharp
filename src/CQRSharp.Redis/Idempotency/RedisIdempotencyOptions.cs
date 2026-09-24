namespace CQRSharp.Redis;

/// <summary>
///     Configuration for the Redis-backed durable idempotency store.
/// </summary>
public sealed class RedisIdempotencyOptions
{
    /// <summary>
    ///     Namespace prefix applied to every Redis key the store uses: two per idempotency key, its value
    ///     (<c>{KeyPrefix}k:{key}</c>) and its payload fingerprint (<c>{KeyPrefix}f:{key}</c>). Isolate independent
    ///     stores by giving them different prefixes. It must contain a non-empty hash tag (a <c>{...}</c> part), which
    ///     keeps a key's value and fingerprint in one Redis Cluster slot; host start fails otherwise. Default:
    ///     <c>{cqrs:idemp}:</c>.
    /// </summary>
    public string KeyPrefix { get; set; } = "{cqrs:idemp}:";

    /// <summary>
    ///     How long a claimed key is remembered, counted from the claim, before Redis expires it. This is exactly the
    ///     deduplication window: a request whose key was claimed longer ago than this is no longer treated as a duplicate,
    ///     and the key of a claimant that crashed frees itself when it ends. The expiry is timed by Redis (no client clock
    ///     is consulted). At least one millisecond. Default: 24 hours.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    ///     The Redis logical database index to use; -1 (the default) selects the connection's default database.
    /// </summary>
    public int Database { get; set; } = -1;
}
