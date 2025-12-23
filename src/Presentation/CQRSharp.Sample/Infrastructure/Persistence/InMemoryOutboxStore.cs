using System.Collections.Concurrent;
using CQRSharp.Abstractions.Data.Interfaces.Outbox;
using CQRSharp.Abstractions.Data.Models.Outbox;

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
        var pending = _messages.Values
            .Where(m => m.Status == OutboxMessageStatus.Pending)
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .ToArray();

        return Task.FromResult<IEnumerable<OutboxMessage>>(pending);
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

    public Task MarkAsFailedAsync(Guid messageId, string? error, CancellationToken cancellationToken)
    {
        if (_messages.TryGetValue(messageId, out var message))
            _messages[messageId] = message with { Status = OutboxMessageStatus.Failed, Error = error };

        return Task.CompletedTask;
    }
}
