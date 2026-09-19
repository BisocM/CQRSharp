namespace CQRSharp.Redis.Outbox;

/// <summary>
///     Configuration for the Redis-backed durable outbox store.
/// </summary>
public sealed class RedisOutboxOptions
{
    /// <summary>
    ///     Namespace prefix applied to every Redis key the store uses (the due sorted set and the per-message hashes).
    ///     Isolate independent outboxes by giving them different prefixes; must be non-empty.
    /// </summary>
    /// <remarks>
    ///     The store's Lua scripts touch several keys under this prefix atomically. On <b>Redis Cluster</b> those keys
    ///     must hash to one slot, which the default guarantees with a hash tag (the <c>{...}</c> part). A custom prefix
    ///     must keep one to work on a cluster. <b>Upgrading from 4.x:</b> the default was <c>cqrsharp:outbox:</c>; set that
    ///     value explicitly to keep draining messages stored under it.
    /// </remarks>
    public string KeyPrefix { get; set; } = "{cqrsharp:outbox}:";

    /// <summary>
    ///     How long a claimed message stays leased before it may be reclaimed. A processor that crashes after claiming
    ///     a message but before finalizing it leaves the message invisible only until this timeout elapses, after which
    ///     it becomes claimable again. Must be greater than zero.
    /// </summary>
    /// <remarks>
    ///     One lease covers a whole claimed batch, which the processor dispatches sequentially, so this must comfortably
    ///     exceed <c>BatchSize × the slowest handler</c>; otherwise a second instance reclaims — and re-delivers — the
    ///     tail of a batch that is still being worked through. The default matches the EF Core store.
    /// </remarks>
    public TimeSpan VisibilityTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     How long the per-message hash is kept after a message reaches a terminal state
    ///     (processed or failed) before Redis expires them, bounding the store's footprint. Must be greater than zero.
    /// </summary>
    public TimeSpan FinalizedRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    ///     The Redis logical database index to use; -1 selects the connection's default database.
    /// </summary>
    public int Database { get; set; } = -1;
}
