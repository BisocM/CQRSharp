using CQRSharp.Pipelines;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Background.Outbox.Types;

/// <summary>
///     A thread-safe in-process outbox store for development, tests, and single-node demos. It is NOT durable: every
///     message lives in process memory and is lost on restart, so it offers no real cross-crash at-least-once
///     guarantee — use a database- or Redis-backed store in production. It honors the full store contract: claims are
///     atomic and carry a token, a message left in progress past the visibility timeout is handed out again under a
///     new claim, and an operation presented with a lost claim changes nothing. Processed messages are evicted, so a
///     long-running node does not grow without bound; dead-lettered (failed) messages are kept for inspection.
/// </summary>
internal sealed class InMemoryOutboxStore : IOutboxStore
{
    // One lock rather than lock-free structures: every operation is a read-check-write on a message's state, and this
    // store is for development and tests, where being obviously correct matters more than contention.
    private readonly object _gate = new();
    private readonly Dictionary<Guid, OutboxMessage> _messages = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _visibilityTimeout;

    public InMemoryOutboxStore(TimeProvider timeProvider, IOptions<InMemoryOutboxStoreOptions> options)
    {
        _timeProvider = timeProvider;
        _visibilityTimeout = options.Value.VisibilityTimeout;
    }

    public Task StoreAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            foreach (var message in messages)
                _messages.TryAdd(message.Id, message with { Claim = null });
        }

        return Task.CompletedTask;
    }

    public Task<IEnumerable<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken)
    {
        var claimed = new List<OutboxMessage>();

        lock (_gate)
        {
            var now = Now();

            // FIFO by creation time; Id breaks ties deterministically so a fixed batch size claims a stable subset.
            var due = _messages.Values
                .Where(m => IsClaimable(m, now))
                .OrderBy(m => m.CreatedAt)
                .ThenBy(m => m.Id)
                .Take(batchSize)
                .ToList();

            foreach (var message in due)
            {
                var inProgress = message with
                {
                    Status = OutboxMessageStatus.InProgress,
                    Claim = new OutboxClaim(message.Id, Guid.NewGuid().ToString("N"), now + _visibilityTimeout)
                };

                _messages[message.Id] = inProgress;
                claimed.Add(inProgress);
            }
        }

        return Task.FromResult<IEnumerable<OutboxMessage>>(claimed);
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
            if (!TryGetOwned(claim, out var message)) return Task.FromResult(0);

            var attempts = message.AttemptCount + 1;
            _messages[claim.MessageId] = message with
            {
                Status = OutboxMessageStatus.Pending,
                AttemptCount = attempts,
                LastError = error,
                NextRetryAt = nextRetryAt,
                Claim = null
            };

            return Task.FromResult(attempts);
        }
    }

    public Task<bool> MarkAsFailedAsync(OutboxClaim claim, string? error, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!TryGetOwned(claim, out var message)) return Task.FromResult(false);

            _messages[claim.MessageId] = message with { Status = OutboxMessageStatus.Failed, LastError = error, Claim = null };
            return Task.FromResult(true);
        }
    }

    public Task<OutboxClaim?> RenewAsync(OutboxClaim claim, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!TryGetOwned(claim, out var message)) return Task.FromResult<OutboxClaim?>(null);

            var renewed = claim with { LeasedUntil = Now() + _visibilityTimeout };
            _messages[claim.MessageId] = message with { Claim = renewed };
            return Task.FromResult<OutboxClaim?>(renewed);
        }
    }

    public Task ReleaseAsync(IReadOnlyCollection<OutboxClaim> claims, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            foreach (var claim in claims)
                if (TryGetOwned(claim, out var message))
                    _messages[claim.MessageId] = message with { Status = OutboxMessageStatus.Pending, Claim = null };
        }

        return Task.CompletedTask;
    }

    /// <summary>A point-in-time copy of every message, for in-process inspection in tests.</summary>
    internal IReadOnlyCollection<OutboxMessage> Snapshot()
    {
        lock (_gate)
        {
            return _messages.Values.ToArray();
        }
    }

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;

    // The caller holds the message only while it is in progress under exactly this token: a reclaim issues a new one.
    private bool TryGetOwned(OutboxClaim claim, out OutboxMessage message)
        => _messages.TryGetValue(claim.MessageId, out message!) &&
           message.Status == OutboxMessageStatus.InProgress &&
           message.Claim?.Token == claim.Token;

    private static bool IsClaimable(OutboxMessage m, DateTime now)
    {
        if (m.Status == OutboxMessageStatus.Pending)
            return m.NextRetryAt is null || m.NextRetryAt <= now;

        // Reclaim a message that has been in progress past its lease: its claimant likely crashed.
        return m.Status == OutboxMessageStatus.InProgress && (m.Claim is null || m.Claim.Value.LeasedUntil <= now);
    }
}
