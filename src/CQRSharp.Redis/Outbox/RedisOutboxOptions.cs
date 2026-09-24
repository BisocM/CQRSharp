namespace CQRSharp.Redis;

/// <summary>
///     Configuration for the Redis-backed durable outbox store and the inbox that pairs with it.
/// </summary>
public sealed class RedisOutboxOptions
{
    /// <summary>
    ///     Namespace prefix applied to every Redis key the store uses (the due sorted set, the per-message hashes, the
    ///     partition sets, the dead-letter set and the inbox records). Isolate independent outboxes by giving them
    ///     different prefixes. It must contain a non-empty hash tag (a <c>{...}</c> part); host start fails otherwise.
    ///     Default: <c>{cqrsharp:outbox}:</c>.
    /// </summary>
    /// <remarks>
    ///     The store's Lua scripts touch several of these keys atomically, which Redis Cluster (and the cluster-aware
    ///     proxies) allows only when they all hash to one slot. The hash tag is what puts them there: Redis slots a key
    ///     with a tag by the tag alone. It is required on a single server too, so an outbox keeps working when it moves
    ///     to a cluster. Changing the prefix does not move messages stored under the old one.
    /// </remarks>
    public string KeyPrefix { get; set; } = "{cqrsharp:outbox}:";

    /// <summary>
    ///     How long a claimed message stays leased before it may be reclaimed. A processor that crashes after claiming
    ///     a message but before finalizing it leaves the message invisible only until this timeout elapses, after which
    ///     it becomes claimable again. At least one millisecond. Default: 5 minutes, as in the EF Core store.
    /// </summary>
    /// <remarks>
    ///     The processor renews a message's lease just before dispatching it once half of the lease has elapsed, but not
    ///     while a handler runs, so a delivery must finish within about half of this timeout: set it comfortably above
    ///     twice your slowest single handler. Longer values only delay recovery after a crash. A message whose lease
    ///     lapsed and was reclaimed before its turn is skipped, not delivered twice.
    /// </remarks>
    public TimeSpan VisibilityTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     How long a <b>dead-lettered</b> message is kept, measured from when it failed. <c>null</c> (the default) keeps
    ///     dead letters until they are requeued or purged: they are the record of what could not be delivered. When set
    ///     (at least one millisecond), expired dead letters are dropped as part of the next claim. A processed message is
    ///     deleted as soon as it is marked processed; the inbox is what recognises a redelivery of it.
    /// </summary>
    public TimeSpan? DeadLetterRetention { get; set; }

    /// <summary>
    ///     How long an inbox record (a completed delivery) is kept, which is how long a redelivery of the same message is
    ///     recognised and skipped. Must comfortably exceed <see cref="VisibilityTimeout" />, and be at least one
    ///     millisecond. Default: 7 days.
    /// </summary>
    public TimeSpan InboxRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    ///     The Redis logical database index to use; -1 (the default) selects the connection's default database.
    /// </summary>
    public int Database { get; set; } = -1;
}
