using CQRSharp.Pipelines;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace CQRSharp.Redis.Idempotency;

/// <summary>
///     A durable <see cref="IIdempotencyStore" /> backed by Redis. Each idempotency key maps to a single Redis key
///     (<c>{KeyPrefix}{key}</c>). A claim is an atomic <c>SET key 1 NX EX retention</c>: the key is created only if it
///     does not already exist, so concurrent claimants race on one server-side operation and exactly one wins. The
///     <c>EX</c> expiry IS the deduplication window — server-timed by Redis — so no client clock (and thus no
///     <see cref="TimeProvider" />) is needed, and a crashed claimant's key self-heals once the retention elapses.
///     The stored value is a per-claim token, so a release only ever deletes the claim it made: a claimant whose claim
///     expired mid-flight (and was taken over) cannot delete its successor's live claim.
/// </summary>
internal sealed class RedisIdempotencyStore : IIdempotencyStore
{
    private readonly IConnectionMultiplexer _mux;
    private readonly string _keyPrefix;
    private readonly int _database;
    private readonly TimeSpan _retention;

    // The token of each claim this process currently holds, so ReleaseAsync(key) can prove ownership to Redis.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _heldClaims = new(StringComparer.Ordinal);

    // The key's value is "p:<token>" while the request is in flight and "c:<result bytes>" once it completed.
    private const string PendingPrefix = "p:";
    private const byte CompletedMarker = (byte)'c';

    // Compare-and-delete: remove the key only while it still carries this claimant's token.
    private const string ReleaseScript = @"
if redis.call('GET', KEYS[1]) == ARGV[1] then
    return redis.call('DEL', KEYS[1])
end
return 0";

    // Compare-and-complete: swap this claimant's in-flight value for the completed one, keeping the key's remaining TTL
    // (the retention window runs from the claim). PTTL + PX rather than KEEPTTL, which needs Redis 6.
    private const string CompleteScript = @"
if redis.call('GET', KEYS[1]) ~= ARGV[1] then return 0 end
local ttl = redis.call('PTTL', KEYS[1])
if ttl > 0 then
    redis.call('SET', KEYS[1], ARGV[2], 'PX', ttl)
else
    redis.call('SET', KEYS[1], ARGV[2])
end
return 1";

    public RedisIdempotencyStore(IConnectionMultiplexer mux, IOptions<RedisIdempotencyOptions> options)
    {
        _mux = mux ?? throw new ArgumentNullException(nameof(mux));
        var opts = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _keyPrefix = opts.KeyPrefix;
        _database = opts.Database;
        _retention = opts.Retention;
    }

    public async Task<IdempotencyClaim> TryClaimAsync(string key, CancellationToken cancellationToken)
    {
        // SET NX EX is atomically duplicate-safe on its own: the write lands iff the key was absent, so the boolean it
        // returns is exactly "newly claimed". The retention TTL doubles as the dedup window and the crash self-heal.
        var db = _mux.GetDatabase(_database);
        var token = PendingPrefix + Guid.NewGuid().ToString("N");
        var redisKey = (RedisKey)(_keyPrefix + key);

        // Bounded loop: the key can expire between a lost SET NX and the GET that inspects the winner.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (await db.StringSetAsync(redisKey, token, _retention, When.NotExists).ConfigureAwait(false))
            {
                _heldClaims[key] = token;
                return IdempotencyClaim.Claimed;
            }

            var existing = (byte[]?)await db.StringGetAsync(redisKey).ConfigureAwait(false);
            if (existing is null) continue;

            if (existing.Length == 0 || existing[0] != CompletedMarker)
                return IdempotencyClaim.InProgress;

            // "c:" followed by the stored result; nothing after the prefix means the request stored none.
            return IdempotencyClaim.Completed(existing.Length > 2 ? existing[2..] : null);
        }

        return IdempotencyClaim.InProgress;
    }

    public async Task CompleteAsync(string key, byte[]? result, CancellationToken cancellationToken)
    {
        // Only a claim this process made, and only while Redis still holds that very claim.
        if (!_heldClaims.TryRemove(key, out var token))
            return;

        var completed = new byte[2 + (result?.Length ?? 0)];
        completed[0] = CompletedMarker;
        completed[1] = (byte)':';
        result?.CopyTo(completed, 2);

        var db = _mux.GetDatabase(_database);
        await db.ScriptEvaluateAsync(CompleteScript, [(RedisKey)(_keyPrefix + key)], [(RedisValue)token, (RedisValue)completed]).ConfigureAwait(false);
    }

    public async Task ReleaseAsync(string key, CancellationToken cancellationToken)
    {
        // Only a claim this process made can be released, and only while Redis still holds that very claim. An unknown,
        // expired or taken-over key is left alone.
        if (!_heldClaims.TryRemove(key, out var token))
            return;

        var db = _mux.GetDatabase(_database);
        await db.ScriptEvaluateAsync(ReleaseScript, [(RedisKey)(_keyPrefix + key)], [(RedisValue)token]).ConfigureAwait(false);
    }
}
