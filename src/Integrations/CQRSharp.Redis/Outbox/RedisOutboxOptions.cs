namespace CQRSharp.Redis.Outbox;

/// <summary>
///     Configuration for the Redis-backed durable outbox store.
/// </summary>
public sealed class RedisOutboxOptions
{
    /// <summary>
    ///     Namespace prefix applied to every Redis key the store uses (the due sorted set, the per-message hashes,
    ///     and the finalized set). Isolate independent outboxes by giving them different prefixes; must be non-empty.
    /// </summary>
    public string KeyPrefix { get; set; } = "cqrsharp:outbox:";

    /// <summary>
    ///     How long a claimed message stays leased before it may be reclaimed. A processor that crashes after claiming
    ///     a message but before finalizing it leaves the message invisible only until this timeout elapses, after which
    ///     it becomes claimable again. Must be greater than zero.
    /// </summary>
    public TimeSpan VisibilityTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     How long the per-message hash and finalized marker are kept after a message reaches a terminal state
    ///     (processed or failed) before Redis expires them, bounding the store's footprint. Must be greater than zero.
    /// </summary>
    public TimeSpan FinalizedRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    ///     The Redis logical database index to use; -1 selects the connection's default database.
    /// </summary>
    public int Database { get; set; } = -1;
}
