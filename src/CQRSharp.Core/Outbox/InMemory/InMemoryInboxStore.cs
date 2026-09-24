using CQRSharp.Persistence;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Outbox;

/// <summary>
///     The in-process <see cref="IInboxStore" /> that pairs with the in-memory outbox store. Not durable, like the
///     store it pairs with; records age out after <see cref="InMemoryOutboxStoreOptions.InboxRetention" />.
/// </summary>
internal sealed class InMemoryInboxStore : IInboxStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(Guid MessageId, string HandlerName), DateTime> _records = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _retention;
    private DateTime _nextSweep;

    public InMemoryInboxStore(TimeProvider timeProvider, IOptions<InMemoryOutboxStoreOptions> options)
    {
        _timeProvider = timeProvider;
        _retention = options.Value.InboxRetention;
    }

    // Process memory is never part of a database transaction: a delivery is recorded after the unit of work commits.
    public bool JoinsUnitOfWork => false;

    public Task<bool> IsDeliveredAsync(Guid messageId, string handlerName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handlerName);
        lock (_gate)
        {
            var now = Now();
            Sweep(now);
            return Task.FromResult(_records.TryGetValue((messageId, handlerName), out var recordedAt) && now - recordedAt < _retention);
        }
    }

    public Task<bool> RecordDeliveryAsync(Guid messageId, string handlerName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handlerName);
        lock (_gate)
        {
            var now = Now();
            Sweep(now);
            var key = (messageId, handlerName);
            if (_records.TryGetValue(key, out var recordedAt) && now - recordedAt < _retention)
                return Task.FromResult(false);

            _records[key] = now;
            return Task.FromResult(true);
        }
    }

    /// <summary>How many records the store holds, expired ones not yet swept included; for in-process inspection in tests.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _records.Count;
            }
        }
    }

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;

    // Swept at most once per retention window so the dictionary does not grow with every delivery ever made.
    private void Sweep(DateTime now)
    {
        if (now < _nextSweep) return;
        _nextSweep = now + _retention;

        List<(Guid, string)>? expired = null;
        foreach (var record in _records)
            if (now - record.Value >= _retention)
                (expired ??= []).Add(record.Key);

        if (expired is null) return;
        foreach (var key in expired) _records.Remove(key);
    }
}
