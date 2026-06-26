using CQRSharp.Abstractions.Interfaces.Idempotency;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace CQRSharp.Redis.Idempotency;

/// <summary>
///     A durable <see cref="IIdempotencyStore" /> backed by Redis. Each idempotency key maps to a single Redis key
///     (<c>{KeyPrefix}{key}</c>). A claim is an atomic <c>SET key 1 NX EX retention</c>: the key is created only if it
///     does not already exist, so concurrent claimants race on one server-side operation and exactly one wins. The
///     <c>EX</c> expiry IS the deduplication window — server-timed by Redis — so no client clock (and thus no
///     <see cref="TimeProvider" />) is needed, and a crashed claimant's key self-heals once the retention elapses.
/// </summary>
internal sealed class RedisIdempotencyStore : IIdempotencyStore
{
    private readonly IConnectionMultiplexer _mux;
    private readonly string _keyPrefix;
    private readonly int _database;
    private readonly TimeSpan _retention;

    public RedisIdempotencyStore(IConnectionMultiplexer mux, IOptions<RedisIdempotencyOptions> options)
    {
        _mux = mux ?? throw new ArgumentNullException(nameof(mux));
        var opts = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _keyPrefix = opts.KeyPrefix;
        _database = opts.Database;
        _retention = opts.Retention;
    }

    public async Task<bool> TryClaimAsync(string key, CancellationToken cancellationToken)
    {
        // SET NX EX is atomically duplicate-safe on its own: the write lands iff the key was absent, so the boolean it
        // returns is exactly "newly claimed". The retention TTL doubles as the dedup window and the crash self-heal.
        var db = _mux.GetDatabase(_database);
        return await db.StringSetAsync(_keyPrefix + key, "1", _retention, When.NotExists).ConfigureAwait(false);
    }

    public async Task ReleaseAsync(string key, CancellationToken cancellationToken)
    {
        // Deleting a missing key is a no-op in Redis, so releasing an unknown (or already-expired) key is harmless.
        var db = _mux.GetDatabase(_database);
        await db.KeyDeleteAsync(_keyPrefix + key).ConfigureAwait(false);
    }
}
