using CQRSharp.Persistence;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Outbox;

/// <summary>
///     A thread-safe in-process outbox store for development, tests, and single-node demos. It is NOT durable: every
///     message lives in process memory and is lost on restart, so it offers no real cross-crash at-least-once
///     guarantee — use a database- or Redis-backed store in production. It honors the full store contract: claims are
///     atomic and carry a token, a message left in progress past the visibility timeout is handed out again under a
///     new claim, an operation presented with a lost claim changes nothing, a partitioned message is held back while
///     an earlier message with the same key and handler is unfinished, and dead letters can be listed, requeued and
///     purged. Processed messages are evicted, so a long-running node does not grow without bound; dead-lettered
///     (failed) messages are kept for inspection until requeued, purged, or aged out by the configured retention.
/// </summary>
internal sealed class InMemoryOutboxStore : IOutboxStore
{
    // One lock rather than lock-free structures: every operation is a read-check-write on a message's state, and this
    // store is for development and tests, where being obviously correct matters more than contention.
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _messages = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _visibilityTimeout;
    private readonly TimeSpan? _deadLetterRetention;
    private long _sequence;

    public InMemoryOutboxStore(TimeProvider timeProvider, IOptions<InMemoryOutboxStoreOptions> options)
    {
        _timeProvider = timeProvider;
        _visibilityTimeout = options.Value.VisibilityTimeout;
        _deadLetterRetention = options.Value.DeadLetterRetention;
    }

    // Process memory is never part of a database transaction: messages are stored after the unit of work commits.
    public bool JoinsUnitOfWork => false;

    public Task StoreAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            foreach (var message in messages)
                // The store order is the tiebreak between messages with the same CreatedAt.
                if (!_messages.ContainsKey(message.Id))
                    _messages.Add(message.Id, new Entry(message, ++_sequence, null));
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimPendingAsync(int batchSize, CancellationToken cancellationToken)
    {
        var claimed = new List<ClaimedOutboxMessage>();

        lock (_gate)
        {
            var now = Now();
            ExpireDeadLetters(now);

            // FIFO by creation time, then by store order. Walking every message in that order lets the partition rule
            // fall out naturally: the first unfinished message of a (key, handler) is its head, and only the head may
            // be claimed, whether or not the head itself is due right now.
            var heads = new HashSet<(string Key, string Handler)>();
            var inFlight = InFlightPartitions(now);
            foreach (var entry in Ordered())
            {
                if (claimed.Count >= batchSize) break;

                var message = entry.Message;
                if (message.Status is OutboxMessageStatus.Processed or OutboxMessageStatus.Failed) continue;

                if (message.PartitionKey is { } key && (!heads.Add((key, message.HandlerName)) || inFlight.Contains((key, message.HandlerName))))
                    continue; // an earlier unfinished message holds this partition, or one of its messages is being delivered

                if (!IsClaimable(entry, now)) continue;

                var inProgress = message with { Status = OutboxMessageStatus.InProgress };
                var claim = new OutboxClaim(message.Id, Guid.NewGuid().ToString("N"), now + _visibilityTimeout);

                _messages[message.Id] = entry with { Message = inProgress, Claim = claim };
                claimed.Add(new ClaimedOutboxMessage(inProgress, claim));
            }
        }

        return Task.FromResult<IReadOnlyList<ClaimedOutboxMessage>>(claimed);
    }

    public Task<bool> MarkAsProcessedAsync(OutboxClaim claim, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!TryGetOwned(claim, out _)) return Task.FromResult(false);

            // A processed message is never read again; evict it rather than keep (and re-sort on every poll) the whole
            // history. A late operation for an evicted id is the documented "unknown message" no-op.
            _messages.Remove(claim.MessageId);
            return Task.FromResult(true);
        }
    }

    public Task<int> IncrementAttemptAsync(OutboxClaim claim, string? error, DateTime? nextRetryAt, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!TryGetOwned(claim, out var entry)) return Task.FromResult(0);

            var attempts = entry.Message.AttemptCount + 1;
            _messages[claim.MessageId] = entry with
            {
                Message = entry.Message with
                {
                    Status = OutboxMessageStatus.Pending,
                    AttemptCount = attempts,
                    LastError = error,
                    NextRetryAt = nextRetryAt
                },
                Claim = null
            };

            return Task.FromResult(attempts);
        }
    }

    public Task<bool> DeferAsync(OutboxClaim claim, DateTime notBefore, string? reason, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!TryGetOwned(claim, out var entry)) return Task.FromResult(false);

            // A failed attempt without the count: nothing was tried. The partition order needs nothing more here, since
            // the claim walks the messages in order and holds a partition behind its first unfinished one.
            _messages[claim.MessageId] = entry with
            {
                Message = entry.Message with
                {
                    Status = OutboxMessageStatus.Pending,
                    LastError = reason,
                    NextRetryAt = notBefore
                },
                Claim = null
            };
            return Task.FromResult(true);
        }
    }

    public Task<bool> MarkAsFailedAsync(OutboxClaim claim, string? error, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!TryGetOwned(claim, out var entry)) return Task.FromResult(false);

            // The attempt that exhausted the budget is an attempt too; count it, as the durable stores do, so the dead
            // letter tells the whole story.
            _messages[claim.MessageId] = entry with
            {
                Message = entry.Message with
                {
                    Status = OutboxMessageStatus.Failed,
                    AttemptCount = entry.Message.AttemptCount + 1,
                    LastError = error,
                    FailedAt = Now(),
                    NextRetryAt = null
                },
                Claim = null
            };
            return Task.FromResult(true);
        }
    }

    public Task<OutboxClaim?> RenewAsync(OutboxClaim claim, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            // A lease that ran out is not renewed even while the claim still matches: the message has been claimable
            // since, and its partition may already be delivering an earlier message that a claim let through.
            var now = Now();
            if (!TryGetOwned(claim, out var entry) || entry.Claim is not { } held || held.LeasedUntil <= now)
                return Task.FromResult<OutboxClaim?>(null);

            var renewed = claim with { LeasedUntil = now + _visibilityTimeout };
            _messages[claim.MessageId] = entry with { Claim = renewed };
            return Task.FromResult<OutboxClaim?>(renewed);
        }
    }

    public Task ReleaseAsync(IReadOnlyCollection<OutboxClaim> claims, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            foreach (var claim in claims)
                if (TryGetOwned(claim, out var entry))
                    _messages[claim.MessageId] = entry with
                    {
                        Message = entry.Message with { Status = OutboxMessageStatus.Pending },
                        Claim = null
                    };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxMessage>> GetDeadLettersAsync(int limit, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<OutboxMessage> deadLetters = _messages.Values
                .Where(e => e.Message.Status == OutboxMessageStatus.Failed)
                .OrderBy(e => e.Message.FailedAt)
                .ThenBy(e => e.Sequence)
                .Take(limit)
                .Select(e => e.Message)
                .ToArray();
            return Task.FromResult(deadLetters);
        }
    }

    public Task<bool> RequeueAsync(Guid messageId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_messages.TryGetValue(messageId, out var entry) || entry.Message.Status != OutboxMessageStatus.Failed)
                return Task.FromResult(false);

            _messages[messageId] = entry with
            {
                Message = entry.Message with
                {
                    Status = OutboxMessageStatus.Pending,
                    AttemptCount = 0,
                    NextRetryAt = null,
                    FailedAt = null
                },
                Claim = null
            };
            return Task.FromResult(true);
        }
    }

    public Task<int> PurgeDeadLettersAsync(DateTime failedBefore, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(RemoveDeadLettersFailedAtOrBefore(failedBefore));
        }
    }

    public Task<OutboxBacklog> GetBacklogAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            long pending = 0, dead = 0;
            DateTime? oldest = null;
            foreach (var entry in _messages.Values)
            {
                switch (entry.Message.Status)
                {
                    case OutboxMessageStatus.Failed:
                        dead++;
                        break;
                    case OutboxMessageStatus.Pending or OutboxMessageStatus.InProgress:
                        pending++;
                        if (oldest is null || entry.Message.CreatedAt < oldest) oldest = entry.Message.CreatedAt;
                        break;
                }
            }

            return Task.FromResult(new OutboxBacklog(pending, dead, oldest));
        }
    }

    /// <summary>A point-in-time copy of every message, for in-process inspection in tests.</summary>
    internal IReadOnlyCollection<OutboxMessage> Snapshot()
    {
        lock (_gate)
        {
            return _messages.Values.Select(e => e.Message).ToArray();
        }
    }

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;

    private IEnumerable<Entry> Ordered()
        => _messages.Values.OrderBy(e => e.Message.CreatedAt).ThenBy(e => e.Sequence).ToList();

    private void ExpireDeadLetters(DateTime now)
    {
        if (_deadLetterRetention is { } retention)
            RemoveDeadLettersFailedAtOrBefore(now - retention);
    }

    // Caller holds the gate.
    private int RemoveDeadLettersFailedAtOrBefore(DateTime cutoff)
    {
        var expired = _messages.Values
            .Where(e => e.Message.Status == OutboxMessageStatus.Failed && e.Message.FailedAt <= cutoff)
            .Select(e => e.Message.Id)
            .ToList();
        foreach (var id in expired) _messages.Remove(id);
        return expired.Count;
    }

    // The caller holds the message only while it is in progress under exactly this token: a reclaim issues a new one.
    private bool TryGetOwned(OutboxClaim claim, out Entry entry)
        => _messages.TryGetValue(claim.MessageId, out entry) &&
           entry.Message.Status == OutboxMessageStatus.InProgress &&
           entry.Claim?.Token == claim.Token;

    // The partitions with a delivery in flight (a claim whose lease has not expired). Such a partition admits nothing
    // else, not even a message that sorts before the one in flight - a requeued dead letter, or a message stored with an
    // older timestamp by a producer whose clock runs behind. Their order cannot be restored, but two deliveries of one
    // partition never run at the same time.
    private HashSet<(string Key, string Handler)> InFlightPartitions(DateTime now)
    {
        var partitions = new HashSet<(string Key, string Handler)>();
        foreach (var entry in _messages.Values)
        {
            var m = entry.Message;
            if (m.PartitionKey is { } key && m.Status == OutboxMessageStatus.InProgress && entry.Claim is { } claim && claim.LeasedUntil > now)
                partitions.Add((key, m.HandlerName));
        }

        return partitions;
    }

    private static bool IsClaimable(Entry entry, DateTime now)
    {
        var m = entry.Message;
        if (m.Status == OutboxMessageStatus.Pending)
            return m.NextRetryAt is null || m.NextRetryAt <= now;

        // Reclaim a message that has been in progress past its lease: its claimant likely crashed.
        return m.Status == OutboxMessageStatus.InProgress && (entry.Claim is null || entry.Claim.Value.LeasedUntil <= now);
    }

    // The lease lives beside the message, not on it: it is this store's bookkeeping, handed out only with a claim.
    private readonly record struct Entry(OutboxMessage Message, long Sequence, OutboxClaim? Claim);
}
