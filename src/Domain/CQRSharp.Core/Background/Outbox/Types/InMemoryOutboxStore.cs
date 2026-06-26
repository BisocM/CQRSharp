using System.Collections.Concurrent;
using CQRSharp.Abstractions.Interfaces.Outbox;
using CQRSharp.Abstractions.Models.Outbox;
using CQRSharp.Core.Options;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Background.Outbox.Types;

/// <summary>
///     A thread-safe in-process outbox store for development, tests, and single-node demos. It is NOT durable: every
///     message lives in process memory and is lost on restart, so it offers no real cross-crash at-least-once
///     guarantee — use a database- or Redis-backed store in production. Claims are atomic (a compare-and-swap against
///     the exact message snapshot, so two concurrent processors can never claim the same message) and honor a
///     visibility timeout, so a message left in-progress by a processor that crashed becomes claimable again once the
///     timeout elapses.
/// </summary>
internal sealed class InMemoryOutboxStore : IOutboxStore
{
    private readonly ConcurrentDictionary<Guid, OutboxMessage> _messages = new();

    // When each in-progress message was claimed, so it can be reclaimed after the visibility timeout. Kept out of
    // OutboxMessage so the public transport record stays minimal and free of store-internal lease state.
    private readonly ConcurrentDictionary<Guid, DateTime> _claimedAt = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _visibilityTimeout;

    public InMemoryOutboxStore(TimeProvider timeProvider, IOptions<InMemoryOutboxStoreOptions> options)
    {
        _timeProvider = timeProvider;
        _visibilityTimeout = options.Value.VisibilityTimeout;
    }

    public Task StoreAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken)
    {
        foreach (var message in messages)
            _messages.TryAdd(message.Id, message);

        return Task.CompletedTask;
    }

    public Task<IEnumerable<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = Now();
        var claimed = new List<OutboxMessage>();

        // FIFO by creation time; Id breaks ties deterministically so a fixed batch size claims a stable subset.
        var candidates = _messages.Values
            .Where(m => IsClaimable(m, now))
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id);

        foreach (var message in candidates)
        {
            if (claimed.Count >= batchSize) break;

            var inProgress = message with { Status = OutboxMessageStatus.InProgress };

            // Compare-and-swap against the exact snapshot we read: only the processor that still sees that snapshot
            // wins the claim; a racing processor's swap fails and it skips the message.
            if (!_messages.TryUpdate(message.Id, inProgress, message)) continue;

            _claimedAt[message.Id] = now;
            claimed.Add(inProgress);
        }

        return Task.FromResult<IEnumerable<OutboxMessage>>(claimed);
    }

    public Task MarkAsProcessedAsync(Guid messageId, CancellationToken cancellationToken)
    {
        TryTransition(messageId, m => IsTerminal(m)
            ? null
            : m with { Status = OutboxMessageStatus.Processed, ProcessedAt = Now() });

        return Task.CompletedTask;
    }

    public Task<int> IncrementAttemptAsync(Guid messageId, string? error, DateTime? nextRetryAt, CancellationToken cancellationToken)
    {
        var newCount = 0;

        TryTransition(messageId, m =>
        {
            // A message that already reached a terminal state must not be resurrected by a late/duplicate attempt.
            if (IsTerminal(m)) return null;

            newCount = m.AttemptCount + 1;
            return m with
            {
                Status = OutboxMessageStatus.Pending,
                AttemptCount = newCount,
                LastError = error,
                NextRetryAt = nextRetryAt
            };
        });

        return Task.FromResult(newCount);
    }

    public Task MarkAsFailedAsync(Guid messageId, string? error, CancellationToken cancellationToken)
    {
        TryTransition(messageId, m => IsTerminal(m)
            ? null
            : m with { Status = OutboxMessageStatus.Failed, LastError = error });

        return Task.CompletedTask;
    }

    /// <summary>A point-in-time copy of every message, for in-process inspection in tests.</summary>
    internal IReadOnlyCollection<OutboxMessage> Snapshot() => _messages.Values.ToArray();

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;

    private static bool IsTerminal(OutboxMessage m)
        => m.Status is OutboxMessageStatus.Processed or OutboxMessageStatus.Failed;

    private bool IsClaimable(OutboxMessage m, DateTime now)
    {
        if (m.Status == OutboxMessageStatus.Pending)
            return m.NextRetryAt is null || m.NextRetryAt <= now;

        // Reclaim a message that has been in-progress past the visibility timeout: its claimant likely crashed.
        return m.Status == OutboxMessageStatus.InProgress
               && _claimedAt.TryGetValue(m.Id, out var claimedAt)
               && claimedAt + _visibilityTimeout <= now;
    }

    // One compare-and-swap attempt loop: read the current snapshot, apply the rule (a null result means "no change"),
    // then swap; retry only on contention. The lease marker is cleared whenever the message leaves the in-progress
    // state so reclaim timing stays correct.
    private void TryTransition(Guid id, Func<OutboxMessage, OutboxMessage?> transform)
    {
        while (_messages.TryGetValue(id, out var current))
        {
            var next = transform(current);
            if (next is null) return;

            if (!_messages.TryUpdate(id, next, current)) continue;

            if (next.Status != OutboxMessageStatus.InProgress)
                _claimedAt.TryRemove(id, out _);

            return;
        }
    }
}
