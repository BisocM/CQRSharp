using CQRSharp.Core.Outbox;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Shared;

/// <summary>
///     An outbox store over the in-memory one that says whether it joins the unit of work, records when it is written
///     in a shared <see cref="TransactionLog" /> (so a test can see whether that was before or after the commit), fails
///     on demand, and can set aside the messages a test inspects instead of storing them, so the processor never claims
///     them.
/// </summary>
/// <param name="joinsUnitOfWork">What <see cref="JoinsUnitOfWork" /> answers.</param>
/// <param name="log">The log each write is recorded in.</param>
/// <param name="time">The in-memory store's clock; a clock of its own when not given.</param>
/// <param name="setAside">The messages kept in <see cref="SetAside" /> rather than stored; none when not given.</param>
public sealed class RecordingOutboxStore(
    bool joinsUnitOfWork,
    TransactionLog log,
    TimeProvider? time = null,
    Func<OutboxMessage, bool>? setAside = null) : IOutboxStore
{
    private readonly InMemoryOutboxStore _inner = new(time ?? new FakeTimeProvider(), Options.Create(new InMemoryOutboxStoreOptions()));
    private readonly List<OutboxMessage> _stored = new();
    private readonly List<OutboxMessage> _setAside = new();

    public bool JoinsUnitOfWork => joinsUnitOfWork;

    /// <summary>Makes every store call throw this until cleared.</summary>
    public Exception? StoreFailure { get; set; }

    /// <summary>Everything written, set-aside messages included, in order.</summary>
    public IReadOnlyList<OutboxMessage> Stored
    {
        get
        {
            lock (_stored) return _stored.ToArray();
        }
    }

    /// <summary>The messages set aside instead of stored, in order.</summary>
    public IReadOnlyList<OutboxMessage> SetAside
    {
        get
        {
            lock (_stored) return _setAside.ToArray();
        }
    }

    /// <summary>What the store holds now: processed messages are evicted.</summary>
    public IReadOnlyCollection<OutboxMessage> Held => _inner.Snapshot();

    public Task StoreAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken)
    {
        if (StoreFailure is { } failure)
        {
            log.Add("store-failed");
            return Task.FromException(failure);
        }

        var list = messages.ToList();
        var kept = setAside is null ? list : list.Where(m => !setAside(m)).ToList();
        lock (_stored)
        {
            _stored.AddRange(list);
            if (setAside is not null) _setAside.AddRange(list.Where(setAside));
        }

        log.Add("store");
        return kept.Count == 0 ? Task.CompletedTask : _inner.StoreAsync(kept, cancellationToken);
    }

    public Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimPendingAsync(int batchSize, CancellationToken cancellationToken) => _inner.ClaimPendingAsync(batchSize, cancellationToken);
    public Task<bool> MarkAsProcessedAsync(OutboxClaim claim, CancellationToken cancellationToken) => _inner.MarkAsProcessedAsync(claim, cancellationToken);
    public Task<int> IncrementAttemptAsync(OutboxClaim claim, string? error, DateTime? nextRetryAt, CancellationToken cancellationToken) => _inner.IncrementAttemptAsync(claim, error, nextRetryAt, cancellationToken);
    public Task<bool> MarkAsFailedAsync(OutboxClaim claim, string? error, CancellationToken cancellationToken) => _inner.MarkAsFailedAsync(claim, error, cancellationToken);
    public Task<OutboxClaim?> RenewAsync(OutboxClaim claim, CancellationToken cancellationToken) => _inner.RenewAsync(claim, cancellationToken);
    public Task<bool> DeferAsync(OutboxClaim claim, DateTime notBefore, string? reason, CancellationToken cancellationToken) => _inner.DeferAsync(claim, notBefore, reason, cancellationToken);
    public Task ReleaseAsync(IReadOnlyCollection<OutboxClaim> claims, CancellationToken cancellationToken) => _inner.ReleaseAsync(claims, cancellationToken);
    public Task<IReadOnlyList<OutboxMessage>> GetDeadLettersAsync(int limit, CancellationToken cancellationToken) => _inner.GetDeadLettersAsync(limit, cancellationToken);
    public Task<bool> RequeueAsync(Guid messageId, CancellationToken cancellationToken) => _inner.RequeueAsync(messageId, cancellationToken);
    public Task<int> PurgeDeadLettersAsync(DateTime failedBefore, CancellationToken cancellationToken) => _inner.PurgeDeadLettersAsync(failedBefore, cancellationToken);
    public Task<OutboxBacklog> GetBacklogAsync(CancellationToken cancellationToken) => _inner.GetBacklogAsync(cancellationToken);
}

/// <summary>An outbox signal that records, in the shared log, when the processor would have been woken.</summary>
public sealed class RecordingOutboxSignal(TransactionLog log) : IOutboxSignal
{
    public void Signal() => log.Add("signal");
}
