using System.Collections.Concurrent;
using CQRSharp.Abstractions.Interfaces.Outbox;
using CQRSharp.Abstractions.Models.Outbox;

namespace CQRSharp.Sample.Infrastructure.Persistence;

public sealed class InMemoryOutboxStore : IOutboxStore
{
    private readonly ConcurrentDictionary<Guid, OutboxMessage> _messages = new();

    public IReadOnlyCollection<OutboxMessage> Snapshot()
        => _messages.Values.ToArray();

    public Task StoreAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken)
    {
        foreach (var message in messages)
            _messages.TryAdd(message.Id, message);

        return Task.CompletedTask;
    }

    public Task<IEnumerable<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var claimed = new List<OutboxMessage>();

        var candidates = _messages.Values
            .Where(m => m.Status == OutboxMessageStatus.Pending && (m.NextRetryAt is null || m.NextRetryAt <= now))
            .OrderBy(m => m.CreatedAt);

        foreach (var message in candidates)
        {
            if (claimed.Count >= batchSize) break;

            // Atomic claim: only succeed if the message is still exactly the Pending snapshot we read, so two
            // concurrent processors can never claim the same message.
            var inProgress = message with { Status = OutboxMessageStatus.InProgress };
            if (_messages.TryUpdate(message.Id, inProgress, message))
                claimed.Add(inProgress);
        }

        return Task.FromResult<IEnumerable<OutboxMessage>>(claimed);
    }

    public Task MarkAsProcessedAsync(Guid messageId, CancellationToken cancellationToken)
    {
        if (_messages.TryGetValue(messageId, out var message))
        {
            _messages[messageId] = message with
            {
                Status = OutboxMessageStatus.Processed,
                ProcessedAt = DateTime.UtcNow
            };
        }

        return Task.CompletedTask;
    }

    public Task<int> IncrementAttemptAsync(Guid messageId, string? error, DateTime? nextRetryAt, CancellationToken cancellationToken)
    {
        while (_messages.TryGetValue(messageId, out var message))
        {
            var updated = message with
            {
                Status = OutboxMessageStatus.Pending,
                AttemptCount = message.AttemptCount + 1,
                LastError = error,
                NextRetryAt = nextRetryAt
            };

            if (_messages.TryUpdate(messageId, updated, message))
                return Task.FromResult(updated.AttemptCount);
        }

        return Task.FromResult(0);
    }

    public Task MarkAsFailedAsync(Guid messageId, string? error, CancellationToken cancellationToken)
    {
        if (_messages.TryGetValue(messageId, out var message))
            _messages[messageId] = message with { Status = OutboxMessageStatus.Failed, LastError = error };

        return Task.CompletedTask;
    }
}
