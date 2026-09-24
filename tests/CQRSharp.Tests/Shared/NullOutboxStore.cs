using CQRSharp.Persistence;

namespace CQRSharp.Tests.Shared;

/// <summary>
///     An outbox store that keeps nothing and never has anything to claim, for a test that needs an outbox store
///     registered — to satisfy a configuration check or to be the one a registration chose — but never uses it.
/// </summary>
public sealed class NullOutboxStore : IOutboxStore
{
    public bool JoinsUnitOfWork => false;
    public Task StoreAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimPendingAsync(int batchSize, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ClaimedOutboxMessage>>([]);
    public Task<bool> MarkAsProcessedAsync(OutboxClaim claim, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<int> IncrementAttemptAsync(OutboxClaim claim, string? error, DateTime? nextRetryAt, CancellationToken cancellationToken) => Task.FromResult(0);
    public Task<bool> DeferAsync(OutboxClaim claim, DateTime notBefore, string? reason, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> MarkAsFailedAsync(OutboxClaim claim, string? error, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<OutboxClaim?> RenewAsync(OutboxClaim claim, CancellationToken cancellationToken) => Task.FromResult<OutboxClaim?>(null);
    public Task ReleaseAsync(IReadOnlyCollection<OutboxClaim> claims, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<IReadOnlyList<OutboxMessage>> GetDeadLettersAsync(int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<OutboxMessage>>([]);
    public Task<bool> RequeueAsync(Guid messageId, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<int> PurgeDeadLettersAsync(DateTime failedBefore, CancellationToken cancellationToken) => Task.FromResult(0);
    public Task<OutboxBacklog> GetBacklogAsync(CancellationToken cancellationToken) => Task.FromResult(OutboxBacklog.Empty);
}
