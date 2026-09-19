using CQRSharp.Pipelines;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace CQRSharp.Redis.Outbox;

/// <summary>
///     A durable <see cref="IOutboxStore" /> backed by Redis. Each message is persisted as a discrete-field hash
///     (the binary payload is stored verbatim, never re-serialized) and a sorted-set entry whose score is the unix
///     time at which the message becomes claimable — its creation time, its back-off time, or, once claimed, its
///     lease horizon. All claim and finalize transitions run as atomic server-side Lua so two processors never claim
///     the same message and a crashed claimant's message reappears only once its lease elapses. Every time decision
///     is driven by the injected <see cref="TimeProvider" />; Redis server time is never consulted.
/// </summary>
internal sealed class RedisOutboxStore : IOutboxStore
{
    private readonly IConnectionMultiplexer _mux;
    private readonly TimeProvider _timeProvider;
    private readonly string _keyPrefix;
    private readonly int _database;
    private readonly long _visibilityMs;
    private readonly long _retentionMs;

    // Due-set scoring. A message's score is the unix-ms time at which it becomes claimable. Messages with no back-off
    // are "ready now" and must be claimable regardless of their CreatedAt (which is only a FIFO ordering key, never a
    // due gate) yet still ordered among themselves by CreatedAt. We map those into a dedicated negative band by adding
    // ReadyBand to their CreatedAt: the result is always far below any real timestamp (so it is always <= now and thus
    // due) and preserves CreatedAt order. Back-off messages keep their real (positive) NextRetryAt score, so they sort
    // after every ready message and only surface once their NextRetryAt has elapsed.
    private const long ReadyBand = -1_000_000_000_000_000L;

    // Field names of the per-message hash. Kept as constants so the mapper and the Lua agree on the exact layout.
    private const string FieldType = "type";
    private const string FieldPayload = "payload";
    private const string FieldCreated = "created";
    private const string FieldStatus = "status";
    private const string FieldProcessed = "processed";
    private const string FieldError = "error";
    private const string FieldAttempts = "attempts";
    private const string FieldNextRetry = "nextRetry";
    private const string FieldTrace = "trace";
    private const string FieldClaim = "claim";

    // Numeric status codes stored in the hash, mirroring the ordinals of OutboxMessageStatus
    // (Pending=0, Processed=1, Failed=2, InProgress=3), so Lua can compare without string parsing. Held as string
    // constants so they can be concatenated into the const-string Lua scripts below; the static checks below keep
    // these literals in lock-step with the enum so a reordering of OutboxMessageStatus fails fast at construction.
    private const string StatusPending = "0";
    private const string StatusProcessed = "1";
    private const string StatusFailed = "2";
    private const string StatusInProgress = "3";

    static RedisOutboxStore()
    {
        // Fail fast if OutboxMessageStatus is ever reordered out from under the string literals the Lua relies on.
        if (StatusPending != ((int)OutboxMessageStatus.Pending).ToString() ||
            StatusProcessed != ((int)OutboxMessageStatus.Processed).ToString() ||
            StatusFailed != ((int)OutboxMessageStatus.Failed).ToString() ||
            StatusInProgress != ((int)OutboxMessageStatus.InProgress).ToString())
            throw new InvalidOperationException(
                "RedisOutboxStore status codes are out of sync with OutboxMessageStatus ordinals.");
    }

    public RedisOutboxStore(IConnectionMultiplexer mux, IOptions<RedisOutboxOptions> options, TimeProvider timeProvider)
    {
        _mux = mux ?? throw new ArgumentNullException(nameof(mux));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        var opts = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _keyPrefix = opts.KeyPrefix;
        _database = opts.Database;
        _visibilityMs = (long)opts.VisibilityTimeout.TotalMilliseconds;
        _retentionMs = (long)opts.FinalizedRetention.TotalMilliseconds;
    }

    // Persist a batch. For each message ZADD the due set at its visibility time (back-off time when set, else
    // creation time, preserving FIFO by CreatedAt) and HSET its discrete-field hash with the payload stored verbatim.
    private const string StoreScript = @"
local prefix = KEYS[1]
local i = 1
while ARGV[i] ~= nil do
    local id = ARGV[i]
    local score = ARGV[i + 1]
    redis.call('ZADD', prefix .. 'due', score, id)
    redis.call('HSET', prefix .. 'msg:' .. id,
        '" + FieldType + @"', ARGV[i + 2],
        '" + FieldPayload + @"', ARGV[i + 3],
        '" + FieldCreated + @"', ARGV[i + 4],
        '" + FieldStatus + @"', ARGV[i + 5],
        '" + FieldProcessed + @"', ARGV[i + 6],
        '" + FieldError + @"', ARGV[i + 7],
        '" + FieldAttempts + @"', ARGV[i + 8],
        '" + FieldNextRetry + @"', ARGV[i + 9],
        '" + FieldTrace + @"', ARGV[i + 10])
    i = i + 11
end
return 1";

    // Shared Lua prelude: "does the caller still hold this message?" — it exists, is in progress, and carries the token
    // the caller was given when it claimed it. A reclaim after the lease expired writes a new token, so a late finalize
    // or attempt report from the previous claimant fails this check and changes nothing.
    private const string OwnedFunction = @"
local function owned(key, token)
    if redis.call('EXISTS', key) == 0 then return false end
    if tonumber(redis.call('HGET', key, '" + FieldStatus + @"')) ~= " + StatusInProgress + @" then return false end
    return redis.call('HGET', key, '" + FieldClaim + @"') == token
end
";

    // The heart: atomically claim due messages. ARGV[1]=now, ARGV[2]=batch, ARGV[3]=lease horizon (now+visibility),
    // ARGV[4]=this claim's token. For each due id we re-ZADD it at the lease horizon (so it stays invisible until the
    // lease elapses or it is finalized), flip its hash status to InProgress, stamp the claim token, and return its full
    // HGETALL. Because the score is rewritten inside the same atomic script, a concurrent claimant scanning the same
    // window cannot also pick it up.
    private const string ClaimScript = @"
local prefix = KEYS[1]
local now = ARGV[1]
local batch = tonumber(ARGV[2])
local lease = ARGV[3]
local token = ARGV[4]
local ids = redis.call('ZRANGEBYSCORE', prefix .. 'due', '-inf', now, 'LIMIT', 0, batch)
local result = {}
for _, id in ipairs(ids) do
    redis.call('ZADD', prefix .. 'due', lease, id)
    redis.call('HSET', prefix .. 'msg:' .. id, '" + FieldStatus + @"', '" + StatusInProgress + @"', '" + FieldClaim + @"', token)
    result[#result + 1] = id
    result[#result + 1] = redis.call('HGETALL', prefix .. 'msg:' .. id)
end
return result";

    // Mark processed (terminal). Returns 1 when finalized, 0 when the message is missing, already terminal, or the claim
    // was lost. Removes the due entry so it is never reclaimed, records the processed timestamp, and applies the
    // retention TTL. ARGV: id, now, retention, token.
    private const string ProcessedScript = OwnedFunction + @"
local prefix = KEYS[1]
local id = ARGV[1]
local key = prefix .. 'msg:' .. id
if not owned(key, ARGV[4]) then return 0 end
redis.call('ZREM', prefix .. 'due', id)
redis.call('HSET', key, '" + FieldStatus + @"', '" + StatusProcessed + @"', '" + FieldProcessed + @"', ARGV[2])
redis.call('HDEL', key, '" + FieldClaim + @"')
redis.call('PEXPIRE', key, tonumber(ARGV[3]))
return 1";

    // Record a failed attempt and reschedule to Pending. Returns the new attempt count, or 0 if the message is missing,
    // terminal, or the claim was lost (so a stale attempt from a stalled claimant can never yank a message out from
    // under the processor that holds it now). ARGV: id, error, nextRetry (unix-ms, or '' for immediately eligible),
    // readyBand, token. A null back-off scores the message into the ready band by its stored CreatedAt, exactly like a
    // freshly stored message.
    private const string IncrementScript = OwnedFunction + @"
local prefix = KEYS[1]
local id = ARGV[1]
local key = prefix .. 'msg:' .. id
if not owned(key, ARGV[5]) then return 0 end
local attempts = tonumber(redis.call('HGET', key, '" + FieldAttempts + @"')) + 1
local nextRetry = ARGV[3]
local score
if nextRetry == '' then
    score = tonumber(redis.call('HGET', key, '" + FieldCreated + @"')) + tonumber(ARGV[4])
else
    score = nextRetry
end
redis.call('HSET', key,
    '" + FieldAttempts + @"', attempts,
    '" + FieldError + @"', ARGV[2],
    '" + FieldNextRetry + @"', nextRetry,
    '" + FieldStatus + @"', '" + StatusPending + @"')
redis.call('HDEL', key, '" + FieldClaim + @"')
redis.call('ZADD', prefix .. 'due', score, id)
return attempts";

    // Dead-letter (terminal). Returns 1 when dead-lettered, 0 when missing, terminal, or the claim was lost. Counts the
    // exhausting attempt, removes the due entry, records the error, and applies the retention TTL.
    // ARGV: id, error, retention, token.
    private const string FailedScript = OwnedFunction + @"
local prefix = KEYS[1]
local id = ARGV[1]
local key = prefix .. 'msg:' .. id
if not owned(key, ARGV[4]) then return 0 end
redis.call('ZREM', prefix .. 'due', id)
redis.call('HINCRBY', key, '" + FieldAttempts + @"', 1)
redis.call('HSET', key, '" + FieldStatus + @"', '" + StatusFailed + @"', '" + FieldError + @"', ARGV[2])
redis.call('HDEL', key, '" + FieldClaim + @"')
redis.call('PEXPIRE', key, tonumber(ARGV[3]))
return 1";

    // Extend the lease: move the due entry to the new horizon. The token is unchanged. ARGV: id, newLease, token.
    private const string RenewScript = OwnedFunction + @"
local prefix = KEYS[1]
local id = ARGV[1]
if not owned(prefix .. 'msg:' .. id, ARGV[3]) then return 0 end
redis.call('ZADD', prefix .. 'due', ARGV[2], id)
return 1";

    // Give claimed messages back without counting an attempt: pending again and immediately due, in CreatedAt order.
    // ARGV: readyBand, then (id, token) pairs. Lost claims are skipped.
    private const string ReleaseScript = OwnedFunction + @"
local prefix = KEYS[1]
local band = tonumber(ARGV[1])
local released = 0
local i = 2
while ARGV[i] ~= nil do
    local id = ARGV[i]
    local key = prefix .. 'msg:' .. id
    if owned(key, ARGV[i + 1]) then
        redis.call('HSET', key, '" + FieldStatus + @"', '" + StatusPending + @"')
        redis.call('HDEL', key, '" + FieldClaim + @"')
        redis.call('ZADD', prefix .. 'due', tonumber(redis.call('HGET', key, '" + FieldCreated + @"')) + band, id)
        released = released + 1
    end
    i = i + 2
end
return released";

    public async Task StoreAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken)
    {
        var list = messages as IReadOnlyList<OutboxMessage> ?? messages.ToList();
        if (list.Count == 0)
            return;

        // Flatten every message into its 11-slot ARGV stride: id, score, then the nine hash fields.
        var args = new RedisValue[list.Count * 11];
        var i = 0;
        foreach (var m in list)
        {
            args[i++] = m.Id.ToString("N");
            args[i++] = DueScore(m.CreatedAt, m.NextRetryAt);
            args[i++] = m.NotificationType;
            args[i++] = m.Payload;
            args[i++] = ToUnixMs(m.CreatedAt);
            args[i++] = (int)m.Status;
            args[i++] = m.ProcessedAt is { } p ? ToUnixMs(p) : RedisValue.EmptyString;
            args[i++] = m.LastError ?? RedisValue.EmptyString;
            args[i++] = m.AttemptCount;
            args[i++] = m.NextRetryAt is { } n ? ToUnixMs(n) : RedisValue.EmptyString;
            args[i++] = m.TraceParent ?? RedisValue.EmptyString;
        }

        await EvalAsync(StoreScript, args).ConfigureAwait(false);
    }

    public async Task<IEnumerable<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = ToUnixMs(_timeProvider.GetUtcNow().UtcDateTime);
        var lease = now + _visibilityMs;
        var token = Guid.NewGuid().ToString("N");
        var result = await EvalAsync(ClaimScript,
            [now, batchSize, lease, token]).ConfigureAwait(false);

        if (result.IsNull)
            return [];

        // The script returns a flat [id, hash, id, hash, ...] array where each hash is itself an array of HGETALL pairs.
        var pairs = (RedisResult[])result!;
        var leasedUntil = FromUnixMs(lease);
        var claimed = new List<OutboxMessage>(pairs.Length / 2);
        for (var i = 0; i + 1 < pairs.Length; i += 2)
        {
            var id = (string)pairs[i]!;
            var hash = (RedisValue[])pairs[i + 1]!;
            var message = MapFromHash(id, hash);
            claimed.Add(message with { Claim = new OutboxClaim(message.Id, token, leasedUntil) });
        }

        return claimed;
    }

    public async Task<bool> MarkAsProcessedAsync(OutboxClaim claim, CancellationToken cancellationToken)
    {
        var now = ToUnixMs(_timeProvider.GetUtcNow().UtcDateTime);
        var result = await EvalAsync(ProcessedScript,
            [claim.MessageId.ToString("N"), now, _retentionMs, claim.Token]).ConfigureAwait(false);
        return (int)result == 1;
    }

    public async Task<int> IncrementAttemptAsync(OutboxClaim claim, string? error, DateTime? nextRetryAt, CancellationToken cancellationToken)
    {
        // A non-null back-off is scheduled at that exact time; a null back-off makes the message immediately eligible
        // and is scored into the ready band by the Lua using the message's stored CreatedAt.
        var nextRetry = nextRetryAt is { } n ? (RedisValue)ToUnixMs(n) : RedisValue.EmptyString;
        var result = await EvalAsync(IncrementScript,
            [claim.MessageId.ToString("N"), error ?? RedisValue.EmptyString, nextRetry, ReadyBand, claim.Token]).ConfigureAwait(false);
        return (int)result;
    }

    public async Task<bool> MarkAsFailedAsync(OutboxClaim claim, string? error, CancellationToken cancellationToken)
    {
        var result = await EvalAsync(FailedScript,
            [claim.MessageId.ToString("N"), error ?? RedisValue.EmptyString, _retentionMs, claim.Token]).ConfigureAwait(false);
        return (int)result == 1;
    }

    public async Task<OutboxClaim?> RenewAsync(OutboxClaim claim, CancellationToken cancellationToken)
    {
        var lease = ToUnixMs(_timeProvider.GetUtcNow().UtcDateTime) + _visibilityMs;
        var result = await EvalAsync(RenewScript,
            [claim.MessageId.ToString("N"), lease, claim.Token]).ConfigureAwait(false);
        return (int)result == 1 ? claim with { LeasedUntil = FromUnixMs(lease) } : null;
    }

    public async Task ReleaseAsync(IReadOnlyCollection<OutboxClaim> claims, CancellationToken cancellationToken)
    {
        if (claims.Count == 0)
            return;

        var args = new RedisValue[1 + claims.Count * 2];
        args[0] = ReadyBand;
        var i = 1;
        foreach (var claim in claims)
        {
            args[i++] = claim.MessageId.ToString("N");
            args[i++] = claim.Token;
        }

        await EvalAsync(ReleaseScript, args).ConfigureAwait(false);
    }

    // Single execution wrapper: try EVALSHA (cached) and fall back to EVAL when the server reports NOSCRIPT, which
    // happens after a server restart or script-cache flush. The single KEYS[1] is always the key prefix.
    private async Task<RedisResult> EvalAsync(string script, RedisValue[] args)
    {
        var db = _mux.GetDatabase(_database);
        var keys = new RedisKey[] { _keyPrefix };
        try
        {
            return await db.ScriptEvaluateAsync(script, keys, args).ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("NOSCRIPT", StringComparison.Ordinal))
        {
            return await db.ScriptEvaluateAsync(script, keys, args).ConfigureAwait(false);
        }
    }

    // Reconstruct an OutboxMessage from a flat HGETALL [field, value, field, value, ...] array.
    private static OutboxMessage MapFromHash(string id, RedisValue[] hash)
    {
        string type = string.Empty;
        byte[] payload = [];
        DateTime created = default;
        var status = OutboxMessageStatus.Pending;
        DateTime? processed = null;
        string? error = null;
        var attempts = 0;
        DateTime? nextRetry = null;
        string? trace = null;

        for (var i = 0; i + 1 < hash.Length; i += 2)
        {
            var field = (string)hash[i]!;
            var value = hash[i + 1];
            switch (field)
            {
                case FieldType:
                    type = (string)value! ?? string.Empty;
                    break;
                case FieldPayload:
                    payload = (byte[]?)value ?? [];
                    break;
                case FieldCreated:
                    created = FromUnixMs((long)value);
                    break;
                case FieldStatus:
                    status = (OutboxMessageStatus)(int)value;
                    break;
                case FieldProcessed:
                    if (!value.IsNullOrEmpty)
                        processed = FromUnixMs((long)value);
                    break;
                case FieldError:
                    if (!value.IsNullOrEmpty)
                        error = (string?)value;
                    break;
                case FieldAttempts:
                    attempts = (int)value;
                    break;
                case FieldNextRetry:
                    if (!value.IsNullOrEmpty)
                        nextRetry = FromUnixMs((long)value);
                    break;
                case FieldTrace:
                    if (!value.IsNullOrEmpty)
                        trace = (string?)value;
                    break;
            }
        }

        return new OutboxMessage(
            Guid.ParseExact(id, "N"),
            type,
            payload,
            created,
            status,
            processed,
            error,
            attempts,
            nextRetry,
            trace);
    }

    // The due-set score for a message: its real back-off time when set, otherwise its CreatedAt mapped into the
    // ready band so it is immediately claimable yet still FIFO-ordered by CreatedAt.
    private static long DueScore(DateTime createdAt, DateTime? nextRetryAt)
        => nextRetryAt is { } n ? ToUnixMs(n) : ToUnixMs(createdAt) + ReadyBand;

    private static long ToUnixMs(DateTime utc)
        => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static DateTime FromUnixMs(long ms)
        => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
}
