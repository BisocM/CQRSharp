using CQRSharp.Persistence;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace CQRSharp.Redis;

/// <summary>
///     A durable <see cref="IOutboxStore" /> backed by Redis. Each message is persisted as a discrete-field hash
///     (the binary payload is stored verbatim, never re-serialized) and a sorted-set entry whose score is the unix
///     time at which the message becomes claimable — ready now, its back-off time, or, once claimed, its lease
///     horizon. Ready messages share one score and are ordered by their member, which encodes the creation time and a
///     store-assigned sequence, so claims are FIFO by <see cref="OutboxMessage.CreatedAt" /> and then by store order.
///     A partitioned message is kept out of the due set until every earlier message with the same key and handler is
///     finished: each (handler, key) has its own sorted set whose head is the only member ever in the due set, and
///     finishing the head promotes the next. All claim and finalize transitions run as atomic server-side Lua so two
///     processors never claim the same message and a crashed claimant's message reappears only once its lease
///     elapses. A processed message is deleted at once; a dead letter stays in the dead-letter set until it is requeued,
///     purged, or expired by <see cref="RedisOutboxOptions.DeadLetterRetention" />. Every time decision is driven by the
///     injected <see cref="TimeProvider" />; Redis server time is never consulted.
/// </summary>
internal sealed class RedisOutboxStore : IOutboxStore
{
    private readonly IConnectionMultiplexer _mux;
    private readonly TimeProvider _timeProvider;
    private readonly string _keyPrefix;
    private readonly int _database;
    private readonly long _visibilityMs;
    private readonly long _deadLetterRetentionMs; // 0: keep dead letters until requeued or purged

    // Due-set scoring. A message's score is the unix-ms time at which it becomes claimable. Ready messages must be
    // claimable regardless of their CreatedAt (which is only an ordering key, never a due gate), so they all share one
    // score far below any real timestamp — the ready band (ReadyBandLiteral below) — and Redis orders them by member
    // instead, which is why the member encodes (CreatedAt, sequence, id). Back-off messages and leased messages keep
    // their real (positive) due time as the score; the claim moves the ones whose time has come into the ready band
    // first, so the batch is FIFO by member rather than by when each became due.

    // Field names of the per-message hash. Kept as constants so the mapper and the Lua agree on the exact layout.
    private const string FieldType = "type";
    private const string FieldHandler = "handler";
    private const string FieldPartition = "partition";
    private const string FieldNotificationId = "nid";
    private const string FieldPayload = "payload";
    private const string FieldCreated = "created";
    private const string FieldSequence = "seq";
    private const string FieldStatus = "status";
    private const string FieldProcessed = "processed";
    private const string FieldError = "error";
    private const string FieldAttempts = "attempts";
    private const string FieldNextRetry = "nextRetry";
    private const string FieldFailed = "failed";
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
        _deadLetterRetentionMs = opts.DeadLetterRetention is { } deadLetterRetention ? (long)deadLetterRetention.TotalMilliseconds : 0;
    }

    // Shared Lua prelude.
    //   member(key, id)   the due-set / partition-set member of a message: zero-padded CreatedAt and sequence, then the
    //                     id, so equal scores sort by (CreatedAt, sequence) and the id can be read back from the tail.
    //   partkey(key)      the partition set of a message (nil when it has no partition key), named by the handler's
    //                     length, the handler and the key, so no two (handler, key) pairs share a set whatever
    //                     characters either contains.
    //   readyscore(key)   the due-set score of a message that is claimable: its back-off time when one is set and
    //                     still ahead, else the ready band.
    //   owned(key, token) "does the caller still hold this message?" — it exists, is in progress, and carries the
    //                     token the caller was given when it claimed it. A reclaim after the lease expired writes a
    //                     new token, so a late finalize or attempt report from the previous claimant fails this check
    //                     and changes nothing.
    //   liveat(part, n)   the n-th member of a partition set whose hash still exists; the ghosts ahead of it (an
    //                     eviction, a manual DEL) are dropped from every set on the way, so they can never block it.
    //   inflight(part)    whether a delivery of this partition is in flight: the claim marks the partition (hash
    //                     'inflight', part -> member); finish and requeue unmark it, and so does the claim that finds
    //                     the marked delivery's lease expired. Nothing of a marked partition enters the due set,
    //                     wherever in the set it sits - not only the member right behind the head.
    //   promote(part)     put the partition's live head into the due set, unless a delivery is in flight.
    //   enqueue(key, id)  join the all set and, for a partitioned message, its partition set, then promote the
    //                     partition: a new head demotes the previous one, and a head that was not due (its predecessor
    //                     turned out to be a ghost) becomes due. The 'partof' hash (id -> partition set) lets the claim
    //                     finish a ghost's partition bookkeeping.
    //   finish(key, id)   remove a terminal message from the due set and its partition set, unmark the partition and
    //                     promote its next message: the promotion that keeps one key strictly in order.
    //   requeue(key, id)  a message going back to pending (a failed attempt, a release): unmark and promote, so the
    //                     partition's earliest pending message - this one or an earlier arrival - becomes due.
    private const string Prelude = @"
local prefix = KEYS[1]
local function member(key, id)
    return string.format('%013d:%016d:', tonumber(redis.call('HGET', key, '" + FieldCreated + @"')), tonumber(redis.call('HGET', key, '" + FieldSequence + @"'))) .. id
end
local function partkey(key)
    local partition = redis.call('HGET', key, '" + FieldPartition + @"')
    if partition == false or partition == '' then return nil end
    local handler = redis.call('HGET', key, '" + FieldHandler + @"')
    return prefix .. 'part:' .. #handler .. ':' .. handler .. ':' .. partition
end
local function readyscore(key, now)
    local nextRetry = redis.call('HGET', key, '" + FieldNextRetry + @"')
    if nextRetry ~= false and nextRetry ~= '' and tonumber(nextRetry) > tonumber(now) then return tonumber(nextRetry) end
    return " + ReadyBandLiteral + @"
end
local function owned(key, token)
    if redis.call('EXISTS', key) == 0 then return false end
    if tonumber(redis.call('HGET', key, '" + FieldStatus + @"')) ~= " + StatusInProgress + @" then return false end
    return redis.call('HGET', key, '" + FieldClaim + @"') == token
end
local function msgkey(m)
    return prefix .. 'msg:' .. string.sub(m, -32)
end
local function inflight(part)
    return redis.call('HEXISTS', prefix .. 'inflight', part) == 1
end
local function unmark(part, m)
    if redis.call('HGET', prefix .. 'inflight', part) == m then redis.call('HDEL', prefix .. 'inflight', part) end
end
local function drop(part, m)
    redis.call('ZREM', part, m)
    redis.call('ZREM', prefix .. 'due', m)
    redis.call('ZREM', prefix .. 'all', m)
    redis.call('HDEL', prefix .. 'partof', string.sub(m, -32))
    unmark(part, m)
end
local function liveat(part, rank)
    while true do
        local found = redis.call('ZRANGE', part, rank, rank)
        if found[1] == nil then return nil end
        if redis.call('EXISTS', msgkey(found[1])) == 1 then return found[1] end
        drop(part, found[1])
    end
end
local function promote(part, now)
    if inflight(part) then return end
    local head = liveat(part, 0)
    if head ~= nil then
        redis.call('ZADD', prefix .. 'due', readyscore(msgkey(head), now), head)
    end
end
local function enqueue(key, id, now)
    local m = member(key, id)
    redis.call('ZADD', prefix .. 'all', tonumber(redis.call('HGET', key, '" + FieldCreated + @"')), m)
    local part = partkey(key)
    if part == nil then
        redis.call('ZADD', prefix .. 'due', readyscore(key, now), m)
        return
    end
    redis.call('ZADD', part, 0, m)
    redis.call('HSET', prefix .. 'partof', id, part)
    if inflight(part) then return end
    if liveat(part, 0) == m then
        local second = liveat(part, 1)
        if second ~= nil then redis.call('ZREM', prefix .. 'due', second) end
    end
    promote(part, now)
end
local function finish(key, id, now)
    local m = member(key, id)
    redis.call('ZREM', prefix .. 'due', m)
    redis.call('ZREM', prefix .. 'all', m)
    local part = partkey(key)
    if part ~= nil then
        redis.call('ZREM', part, m)
        redis.call('HDEL', prefix .. 'partof', id)
        unmark(part, m)
        promote(part, now)
    end
end
local function requeue(key, id, now)
    local m = member(key, id)
    local part = partkey(key)
    if part == nil then
        redis.call('ZADD', prefix .. 'due', readyscore(key, now), m)
    else
        redis.call('ZREM', prefix .. 'due', m)
        unmark(part, m)
        promote(part, now)
    end
end
";

    private const string ReadyBandLiteral = "-1000000000000000000";

    // Persist a batch. ARGV[1] = now; then, per message, a 13-slot stride: the id and the twelve hash fields. Each
    // message takes the next sequence number, which orders equal CreatedAt values by store order. A partitioned message
    // joins its partition set and enters the due set only when it is that partition's head; anything else waits to be
    // promoted when the messages ahead of it finish.
    private const string StoreScript = Prelude + @"
local now = ARGV[1]
local i = 2
while ARGV[i] ~= nil do
    local id = ARGV[i]
    local key = prefix .. 'msg:' .. id
    if redis.call('EXISTS', key) == 0 then
        local seq = redis.call('INCR', prefix .. 'seq')
        redis.call('HSET', key,
            '" + FieldType + @"', ARGV[i + 1],
            '" + FieldHandler + @"', ARGV[i + 2],
            '" + FieldPartition + @"', ARGV[i + 3],
            '" + FieldNotificationId + @"', ARGV[i + 4],
            '" + FieldPayload + @"', ARGV[i + 5],
            '" + FieldCreated + @"', ARGV[i + 6],
            '" + FieldSequence + @"', seq,
            '" + FieldStatus + @"', ARGV[i + 7],
            '" + FieldProcessed + @"', ARGV[i + 8],
            '" + FieldError + @"', ARGV[i + 9],
            '" + FieldAttempts + @"', ARGV[i + 10],
            '" + FieldNextRetry + @"', ARGV[i + 11],
            '" + FieldTrace + @"', ARGV[i + 12],
            '" + FieldFailed + @"', '')
        local status = tonumber(ARGV[i + 7])
        if status == " + StatusPending + @" or status == " + StatusInProgress + @" then
            enqueue(key, id, now)
        end
    end
    i = i + 13
end
return 1";

    // The heart: atomically claim due messages. ARGV[1]=now, ARGV[2]=batch, ARGV[3]=lease horizon (now+visibility),
    // ARGV[4]=this claim's token, ARGV[5]=the dead-letter expiry cut-off ('' to keep them). Everything in the due set
    // is claimable by construction (a partition's blocked messages are not in it). Members whose due time has elapsed
    // (a back-off, an expired lease) first rejoin the ready band, so the batch is FIFO by member rather than by when
    // each became due - except a partition's delivery in flight whose lease expired: that frees the partition, which is
    // promoted instead, so a message of the partition that arrived ahead of the expired one while it was leased is the
    // one that becomes due, as if the expired one had been released. For each claimed member we re-ZADD it at the
    // lease horizon (so it stays invisible until the lease elapses or it is finalized), flip its hash status to
    // InProgress, stamp the claim token, and return its full HGETALL. A member whose hash is gone (an eviction, a
    // manual DEL) is dropped instead of being handed out as an empty message. Because the score is rewritten inside the
    // same atomic script, a concurrent claimant scanning the same window cannot also pick it up.
    private const string ClaimScript = Prelude + @"
local now = ARGV[1]
local batch = tonumber(ARGV[2])
local lease = ARGV[3]
local token = ARGV[4]
if ARGV[5] ~= '' then
    local expired = redis.call('ZRANGEBYSCORE', prefix .. 'dead', '-inf', ARGV[5])
    for _, deadId in ipairs(expired) do
        redis.call('DEL', prefix .. 'msg:' .. deadId)
        redis.call('ZREM', prefix .. 'dead', deadId)
    end
end
local elapsed = redis.call('ZRANGEBYSCORE', prefix .. 'due', 0, now)
for _, m in ipairs(elapsed) do
    local part = redis.call('HGET', prefix .. 'partof', string.sub(m, -32))
    if part ~= false and redis.call('HGET', prefix .. 'inflight', part) == m then
        unmark(part, m)
        redis.call('ZREM', prefix .. 'due', m)
        promote(part, now)
    else
        redis.call('ZADD', prefix .. 'due', " + ReadyBandLiteral + @", m)
    end
end
local result = {}
local taken = 0
while taken < batch do
    -- Always from the front: every member visited below leaves the window (claimed at the lease horizon, or dropped).
    local members = redis.call('ZRANGEBYSCORE', prefix .. 'due', '-inf', now, 'LIMIT', 0, batch - taken)
    if #members == 0 then break end
    for _, m in ipairs(members) do
        local id = string.sub(m, -32)
        local key = prefix .. 'msg:' .. id
        if redis.call('EXISTS', key) == 1 then
            redis.call('ZADD', prefix .. 'due', lease, m)
            redis.call('HSET', key, '" + FieldStatus + @"', '" + StatusInProgress + @"', '" + FieldClaim + @"', token)
            local part = partkey(key)
            if part ~= nil then redis.call('HSET', prefix .. 'inflight', part, m) end
            result[#result + 1] = id
            result[#result + 1] = redis.call('HGETALL', key)
            taken = taken + 1
        else
            local part = redis.call('HGET', prefix .. 'partof', id)
            if part == false then
                redis.call('ZREM', prefix .. 'due', m)
                redis.call('ZREM', prefix .. 'all', m)
            else
                drop(part, m)
                promote(part, now)
            end
        end
    end
end
return result";

    // Mark processed (terminal). Returns 1 when finalized, 0 when the message is missing, already terminal, or the claim
    // was lost. Leaves the due set and the partition (promoting the next message) and deletes the message: nothing reads
    // a processed message again, a late operation on it is the unknown-message no-op, and a redelivery is recognised by
    // the inbox. ARGV: id, now, token.
    private const string ProcessedScript = Prelude + @"
local id = ARGV[1]
local key = prefix .. 'msg:' .. id
if not owned(key, ARGV[3]) then return 0 end
finish(key, id, ARGV[2])
redis.call('DEL', key)
return 1";

    // Record a failed attempt and reschedule to Pending. Returns the new attempt count, or 0 if the message is missing,
    // terminal, or the claim was lost (so a stale attempt from a stalled claimant can never yank a message out from
    // under the processor that holds it now). The message re-enters the due set at its back-off time, or right away
    // when none is given - unless an earlier message of its partition arrived meanwhile, in which case that one is the
    // head and takes its place. ARGV: id, error, nextRetry (unix-ms, or '' for immediately eligible), now, token.
    private const string IncrementScript = Prelude + @"
local id = ARGV[1]
local key = prefix .. 'msg:' .. id
if not owned(key, ARGV[5]) then return 0 end
local attempts = tonumber(redis.call('HGET', key, '" + FieldAttempts + @"')) + 1
redis.call('HSET', key,
    '" + FieldAttempts + @"', attempts,
    '" + FieldError + @"', ARGV[2],
    '" + FieldNextRetry + @"', ARGV[3],
    '" + FieldStatus + @"', '" + StatusPending + @"')
redis.call('HDEL', key, '" + FieldClaim + @"')
requeue(key, id, ARGV[4])
return attempts";

    // Defer: the increment above without the count - nothing was tried. Returns 1 when deferred, 0 when the message is
    // missing, terminal, or the claim was lost. The message re-enters the due set at its not-before time, or gives the
    // partition's head position to an earlier message that arrived meanwhile. ARGV: id, reason, notBefore (unix-ms),
    // now, token.
    private const string DeferScript = Prelude + @"
local id = ARGV[1]
local key = prefix .. 'msg:' .. id
if not owned(key, ARGV[5]) then return 0 end
redis.call('HSET', key,
    '" + FieldError + @"', ARGV[2],
    '" + FieldNextRetry + @"', ARGV[3],
    '" + FieldStatus + @"', '" + StatusPending + @"')
redis.call('HDEL', key, '" + FieldClaim + @"')
requeue(key, id, ARGV[4])
return 1";

    // Dead-letter (terminal). Returns 1 when dead-lettered, 0 when missing, terminal, or the claim was lost. Counts the
    // exhausting attempt, leaves the due set and the partition (promoting the next message), records the error and the
    // failure time, and joins the dead-letter set (scored by failure time) it is listed, requeued and purged from.
    // ARGV: id, error, now, token.
    private const string FailedScript = Prelude + @"
local id = ARGV[1]
local key = prefix .. 'msg:' .. id
if not owned(key, ARGV[4]) then return 0 end
finish(key, id, ARGV[3])
redis.call('HINCRBY', key, '" + FieldAttempts + @"', 1)
redis.call('HSET', key, '" + FieldStatus + @"', '" + StatusFailed + @"', '" + FieldError + @"', ARGV[2], '" + FieldFailed + @"', ARGV[3], '" + FieldNextRetry + @"', '')
redis.call('HDEL', key, '" + FieldClaim + @"')
redis.call('ZADD', prefix .. 'dead', tonumber(ARGV[3]), id)
return 1";

    // The dead letters, oldest first, as flat [id, hash, id, hash, ...]. ARGV: limit.
    private const string DeadLettersScript = @"
local prefix = KEYS[1]
local ids = redis.call('ZRANGE', prefix .. 'dead', 0, tonumber(ARGV[1]) - 1)
local result = {}
for _, id in ipairs(ids) do
    if redis.call('EXISTS', prefix .. 'msg:' .. id) == 1 then
        result[#result + 1] = id
        result[#result + 1] = redis.call('HGETALL', prefix .. 'msg:' .. id)
    end
end
return result";

    // Requeue a dead letter: a fresh budget, back into the due set / its partition by its original creation time.
    // Returns 1 when requeued, 0 when the message is missing or not dead-lettered. ARGV: id, now.
    private const string RequeueScript = Prelude + @"
local id = ARGV[1]
local key = prefix .. 'msg:' .. id
if redis.call('EXISTS', key) == 0 then return 0 end
if tonumber(redis.call('HGET', key, '" + FieldStatus + @"')) ~= " + StatusFailed + @" then return 0 end
redis.call('ZREM', prefix .. 'dead', id)
redis.call('HSET', key, '" + FieldStatus + @"', '" + StatusPending + @"', '" + FieldAttempts + @"', 0, '" + FieldNextRetry + @"', '', '" + FieldFailed + @"', '')
enqueue(key, id, ARGV[2])
return 1";

    // Delete the dead letters that failed at or before the cut-off. Returns the number deleted. ARGV: cutoff.
    private const string PurgeDeadLettersScript = @"
local prefix = KEYS[1]
local ids = redis.call('ZRANGEBYSCORE', prefix .. 'dead', '-inf', ARGV[1])
for _, id in ipairs(ids) do
    redis.call('DEL', prefix .. 'msg:' .. id)
    redis.call('ZREM', prefix .. 'dead', id)
end
return #ids";

    // The backlog: [pending count, dead-letter count, oldest pending creation ms or ''].
    private const string BacklogScript = @"
local prefix = KEYS[1]
local oldest = redis.call('ZRANGE', prefix .. 'all', 0, 0, 'WITHSCORES')
return {redis.call('ZCARD', prefix .. 'all'), redis.call('ZCARD', prefix .. 'dead'), oldest[2] or ''}";

    // Extend the lease: move the due entry to the new horizon. The token is unchanged. Returns 0 when the claim was lost,
    // and when its lease already ran out (the due entry is no longer ahead of now - a claim since may have moved it into
    // the ready band, or freed its partition for an earlier message): the message has been claimable since, so the late
    // claimant must not dispatch it. ARGV: id, newLease, token, now.
    private const string RenewScript = Prelude + @"
local id = ARGV[1]
local key = prefix .. 'msg:' .. id
if not owned(key, ARGV[3]) then return 0 end
local m = member(key, id)
local leased = redis.call('ZSCORE', prefix .. 'due', m)
if leased == false or tonumber(leased) <= tonumber(ARGV[4]) then return 0 end
redis.call('ZADD', prefix .. 'due', ARGV[2], m)
return 1";

    // Give claimed messages back without counting an attempt: pending again and immediately due, in FIFO order.
    // ARGV: now, then (id, token) pairs. Lost claims are skipped.
    private const string ReleaseScript = Prelude + @"
local now = ARGV[1]
local released = 0
local i = 2
while ARGV[i] ~= nil do
    local id = ARGV[i]
    local key = prefix .. 'msg:' .. id
    if owned(key, ARGV[i + 1]) then
        redis.call('HSET', key, '" + FieldStatus + @"', '" + StatusPending + @"')
        redis.call('HDEL', key, '" + FieldClaim + @"')
        requeue(key, id, now)
        released = released + 1
    end
    i = i + 2
end
return released";

    // Redis never takes part in a database transaction: a request's messages are stored after its unit of work commits,
    // so a rolled-back request publishes nothing and no message is claimable before the data it announces is visible.
    public bool JoinsUnitOfWork => false;

    public async Task StoreAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken)
    {
        var list = messages as IReadOnlyList<OutboxMessage> ?? messages.ToList();
        if (list.Count == 0)
            return;

        // ARGV[1] is now; then every message is flattened into its 13-slot stride (see StoreScript).
        var args = new RedisValue[1 + list.Count * 13];
        args[0] = ToUnixMs(_timeProvider.GetUtcNow().UtcDateTime);
        var i = 1;
        foreach (var m in list)
        {
            args[i++] = m.Id.ToString("N");
            args[i++] = m.NotificationType;
            args[i++] = m.HandlerName;
            args[i++] = m.PartitionKey ?? RedisValue.EmptyString;
            args[i++] = m.NotificationId is { } nid ? nid.ToString("N") : RedisValue.EmptyString;
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

    public async Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimPendingAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = ToUnixMs(_timeProvider.GetUtcNow().UtcDateTime);
        var lease = now + _visibilityMs;
        var token = Guid.NewGuid().ToString("N");
        var deadLetterCutoff = _deadLetterRetentionMs > 0 ? (RedisValue)(now - _deadLetterRetentionMs) : RedisValue.EmptyString;
        var result = await EvalAsync(ClaimScript,
            [now, batchSize, lease, token, deadLetterCutoff]).ConfigureAwait(false);

        if (result.IsNull)
            return [];

        // The script returns a flat [id, hash, id, hash, ...] array where each hash is itself an array of HGETALL pairs.
        var pairs = (RedisResult[])result!;
        var leasedUntil = FromUnixMs(lease);
        var claimed = new List<ClaimedOutboxMessage>(pairs.Length / 2);
        for (var i = 0; i + 1 < pairs.Length; i += 2)
        {
            var id = (string)pairs[i]!;
            var hash = (RedisValue[])pairs[i + 1]!;
            var message = MapFromHash(id, hash);
            claimed.Add(new ClaimedOutboxMessage(message, new OutboxClaim(message.Id, token, leasedUntil)));
        }

        return claimed;
    }

    public async Task<bool> MarkAsProcessedAsync(OutboxClaim claim, CancellationToken cancellationToken)
    {
        var now = ToUnixMs(_timeProvider.GetUtcNow().UtcDateTime);
        var result = await EvalAsync(ProcessedScript,
            [claim.MessageId.ToString("N"), now, claim.Token]).ConfigureAwait(false);
        return (int)result == 1;
    }

    public async Task<int> IncrementAttemptAsync(OutboxClaim claim, string? error, DateTime? nextRetryAt, CancellationToken cancellationToken)
    {
        // A non-null back-off is scheduled at that exact time; a null back-off makes the message immediately eligible.
        var nextRetry = nextRetryAt is { } n ? (RedisValue)ToUnixMs(n) : RedisValue.EmptyString;
        var now = ToUnixMs(_timeProvider.GetUtcNow().UtcDateTime);
        var result = await EvalAsync(IncrementScript,
            [claim.MessageId.ToString("N"), error ?? RedisValue.EmptyString, nextRetry, now, claim.Token]).ConfigureAwait(false);
        return (int)result;
    }

    public async Task<bool> DeferAsync(OutboxClaim claim, DateTime notBefore, string? reason, CancellationToken cancellationToken)
    {
        var now = ToUnixMs(_timeProvider.GetUtcNow().UtcDateTime);
        var result = await EvalAsync(DeferScript,
            [claim.MessageId.ToString("N"), reason ?? RedisValue.EmptyString, ToUnixMs(notBefore), now, claim.Token]).ConfigureAwait(false);
        return (int)result == 1;
    }

    public async Task<bool> MarkAsFailedAsync(OutboxClaim claim, string? error, CancellationToken cancellationToken)
    {
        var now = ToUnixMs(_timeProvider.GetUtcNow().UtcDateTime);
        var result = await EvalAsync(FailedScript,
            [claim.MessageId.ToString("N"), error ?? RedisValue.EmptyString, now, claim.Token]).ConfigureAwait(false);
        return (int)result == 1;
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetDeadLettersAsync(int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0)
            return [];

        var result = await EvalAsync(DeadLettersScript, [limit]).ConfigureAwait(false);
        if (result.IsNull)
            return [];

        var pairs = (RedisResult[])result!;
        var deadLetters = new List<OutboxMessage>(pairs.Length / 2);
        for (var i = 0; i + 1 < pairs.Length; i += 2)
            deadLetters.Add(MapFromHash((string)pairs[i]!, (RedisValue[])pairs[i + 1]!));

        return deadLetters;
    }

    public async Task<bool> RequeueAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var now = ToUnixMs(_timeProvider.GetUtcNow().UtcDateTime);
        var result = await EvalAsync(RequeueScript, [messageId.ToString("N"), now]).ConfigureAwait(false);
        return (int)result == 1;
    }

    public async Task<int> PurgeDeadLettersAsync(DateTime failedBefore, CancellationToken cancellationToken)
    {
        var result = await EvalAsync(PurgeDeadLettersScript, [ToUnixMs(failedBefore)]).ConfigureAwait(false);
        return (int)result;
    }

    public async Task<OutboxBacklog> GetBacklogAsync(CancellationToken cancellationToken)
    {
        var result = (RedisResult[])(await EvalAsync(BacklogScript, []).ConfigureAwait(false))!;
        var oldest = (RedisValue)result[2];
        return new OutboxBacklog(
            (long)result[0],
            (long)result[1],
            oldest.IsNullOrEmpty ? null : FromUnixMs((long)oldest));
    }

    public async Task<OutboxClaim?> RenewAsync(OutboxClaim claim, CancellationToken cancellationToken)
    {
        var now = ToUnixMs(_timeProvider.GetUtcNow().UtcDateTime);
        var lease = now + _visibilityMs;
        var result = await EvalAsync(RenewScript,
            [claim.MessageId.ToString("N"), lease, claim.Token, now]).ConfigureAwait(false);
        return (int)result == 1 ? claim with { LeasedUntil = FromUnixMs(lease) } : null;
    }

    public async Task ReleaseAsync(IReadOnlyCollection<OutboxClaim> claims, CancellationToken cancellationToken)
    {
        if (claims.Count == 0)
            return;

        var args = new RedisValue[1 + claims.Count * 2];
        args[0] = ToUnixMs(_timeProvider.GetUtcNow().UtcDateTime);
        var i = 1;
        foreach (var claim in claims)
        {
            args[i++] = claim.MessageId.ToString("N");
            args[i++] = claim.Token;
        }

        await EvalAsync(ReleaseScript, args).ConfigureAwait(false);
    }

    // The single KEYS[1] is always the key prefix.
    private Task<RedisResult> EvalAsync(string script, RedisValue[] args)
        => RedisScripts.EvaluateAsync(_mux.GetDatabase(_database), script, [_keyPrefix], args);

    // Reconstruct an OutboxMessage from a flat HGETALL [field, value, field, value, ...] array.
    private static OutboxMessage MapFromHash(string id, RedisValue[] hash)
    {
        string type = string.Empty;
        string handler = string.Empty;
        string? partition = null;
        Guid? notificationId = null;
        byte[] payload = [];
        DateTime created = default;
        var status = OutboxMessageStatus.Pending;
        DateTime? processed = null;
        string? error = null;
        var attempts = 0;
        DateTime? nextRetry = null;
        DateTime? failed = null;
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
                case FieldHandler:
                    handler = (string)value! ?? string.Empty;
                    break;
                case FieldPartition:
                    if (!value.IsNullOrEmpty)
                        partition = (string?)value;
                    break;
                case FieldNotificationId:
                    if (!value.IsNullOrEmpty && Guid.TryParseExact((string?)value, "N", out var nid))
                        notificationId = nid;
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
                case FieldFailed:
                    if (!value.IsNullOrEmpty)
                        failed = FromUnixMs((long)value);
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
            handler,
            payload,
            created,
            status,
            processed,
            error,
            attempts,
            nextRetry,
            trace,
            partition,
            notificationId,
            failed);
    }

    private static long ToUnixMs(DateTime utc)
        => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static DateTime FromUnixMs(long ms)
        => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
}
