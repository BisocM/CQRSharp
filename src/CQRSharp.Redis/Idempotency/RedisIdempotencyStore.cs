using CQRSharp.Persistence;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace CQRSharp.Redis;

/// <summary>
///     A durable <see cref="IIdempotencyStore" /> backed by Redis. Each idempotency key maps to two Redis keys: its value
///     (<c>{KeyPrefix}k:{key}</c>, holding <c>p:&lt;token&gt;</c> while the request is in flight and the completed form
///     afterwards) and its payload fingerprint (<c>{KeyPrefix}f:{key}</c>). A claim is an atomic <c>SET … NX PX
///     retention</c> run inside a Lua script together with the fingerprint write: the value is created only if it does
///     not already exist, so concurrent claimants race on one server-side operation and exactly one wins. The <c>PX</c>
///     expiry IS the deduplication window, timed by Redis, so no client clock (and thus no <see cref="TimeProvider" />) is
///     needed, and a crashed claimant's key frees itself once the retention elapses. The value's token makes completion
///     and release compare-and-swap operations: a claimant whose claim expired mid-flight (and was taken over) can
///     neither complete nor delete its successor's claim. The fingerprint carries the same expiry, so a key reused with a
///     different payload is reported as a mismatch for as long as the claim is remembered.
/// </summary>
internal sealed class RedisIdempotencyStore : IIdempotencyStore
{
    private readonly IConnectionMultiplexer _mux;
    private readonly string _keyPrefix;
    private readonly int _database;
    private readonly TimeSpan _retention;

    // The value is "p:<token>" while the request is in flight. Once it completed it is "c" when the request stored no
    // result and "c:<result bytes>" when it stored one - an empty result included, which is "c:" alone.
    private const string PendingPrefix = "p:";
    private const byte CompletedMarker = (byte)'c';
    private const byte ResultSeparator = (byte)':';

    // Claim: SET NX PX is atomically duplicate-safe on its own (the write lands iff the key was absent). Alongside it the
    // payload fingerprint is kept in a sibling key with the same expiry. A losing claim compares its fingerprint with the
    // stored one and returns [status, value]: 1 claimed, 2 payload mismatch, 0 held - with the held value, which the GET
    // always finds: the SET NX failed because the key exists, and Redis freezes key expiry while a script runs.
    // KEYS: value key, fingerprint key. ARGV: token, ttl ms, fingerprint ('' for none). The two keys are
    // "{prefix}k:{key}" and "{prefix}f:{key}": a fixed-position discriminator, so no user key can alias another's
    // fingerprint, and the prefix's hash tag keeps both in one cluster slot.
    private const string ClaimScript = @"
if redis.call('SET', KEYS[1], ARGV[1], 'NX', 'PX', ARGV[2]) then
    if ARGV[3] ~= '' then redis.call('SET', KEYS[2], ARGV[3], 'PX', ARGV[2]) end
    return {1, ''}
end
if ARGV[3] ~= '' then
    local stored = redis.call('GET', KEYS[2])
    if stored and stored ~= ARGV[3] then return {2, ''} end
end
return {0, redis.call('GET', KEYS[1])}";

    // Compare-and-delete: remove the key (and its fingerprint) only while it still carries this claimant's token.
    private const string ReleaseScript = @"
if redis.call('GET', KEYS[1]) == ARGV[1] then
    redis.call('DEL', KEYS[2])
    return redis.call('DEL', KEYS[1])
end
return 0";

    // Compare-and-complete: swap this claimant's in-flight value for the completed one, keeping the key's remaining TTL
    // (the retention window runs from the claim). PTTL + PX rather than KEEPTTL, which needs Redis 6.
    private const string CompleteScript = @"
if redis.call('GET', KEYS[1]) ~= ARGV[1] then return 0 end
local ttl = redis.call('PTTL', KEYS[1])
if ttl >= 0 then
    redis.call('SET', KEYS[1], ARGV[2], 'PX', math.max(ttl, 1))
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

    public async Task<IdempotencyClaim> TryClaimAsync(string key, string? fingerprint, CancellationToken cancellationToken)
    {
        // The retention TTL doubles as the dedup window and the crash self-heal; the fingerprint expires with the claim.
        var db = _mux.GetDatabase(_database);
        var token = PendingPrefix + Guid.NewGuid().ToString("N");
        var result = (RedisResult[])(await RedisScripts.EvaluateAsync(
            db,
            ClaimScript,
            [ValueKey(key), FingerprintKey(key)],
            [token, (long)_retention.TotalMilliseconds, fingerprint ?? string.Empty]).ConfigureAwait(false))!;

        switch ((int)result[0])
        {
            case 1:
                // The pending value is the claim's identity: completion and release compare-and-swap on it.
                return IdempotencyClaim.ClaimedWith(token);
            case 2:
                return IdempotencyClaim.PayloadMismatch;
        }

        var existing = (byte[]?)result[1];
        if (existing is null || existing.Length == 0 || existing[0] != CompletedMarker)
            return IdempotencyClaim.InProgress;

        // "c" alone: the request stored no result; "c:" and what follows (possibly nothing): the result it stored.
        return IdempotencyClaim.Completed(existing.Length >= 2 ? existing[2..] : null);
    }

    public async Task CompleteAsync(string key, string claimToken, byte[]? result, CancellationToken cancellationToken)
    {
        // Only while Redis still holds that very claim: a key taken over since carries another token.
        if (string.IsNullOrEmpty(claimToken)) return;

        byte[] completed;
        if (result is null)
        {
            completed = [CompletedMarker];
        }
        else
        {
            completed = new byte[2 + result.Length];
            completed[0] = CompletedMarker;
            completed[1] = ResultSeparator;
            result.CopyTo(completed, 2);
        }

        var db = _mux.GetDatabase(_database);
        await RedisScripts.EvaluateAsync(db, CompleteScript, [ValueKey(key)], [claimToken, completed]).ConfigureAwait(false);
    }

    public async Task ReleaseAsync(string key, string claimToken, CancellationToken cancellationToken)
    {
        // Only while Redis still holds that very claim: an unknown, expired or taken-over key is left alone.
        if (string.IsNullOrEmpty(claimToken)) return;

        var db = _mux.GetDatabase(_database);
        await RedisScripts.EvaluateAsync(db, ReleaseScript, [ValueKey(key), FingerprintKey(key)], [claimToken]).ConfigureAwait(false);
    }

    private RedisKey ValueKey(string key) => _keyPrefix + "k:" + key;

    private RedisKey FingerprintKey(string key) => _keyPrefix + "f:" + key;
}
