namespace CQRSharp.Pipelines;

/// <summary>
///     Defines the contract for a persistence store for outbox messages.
///     Implement this interface to provide a storage mechanism (e.g., using EF Core, Dapper) for notifications
///     that are processed via the outbox pattern. The implementation should ensure that storing messages
///     is atomic with the primary business transaction.
/// </summary>
/// <remarks>
///     The outbox provides <b>at-least-once</b> delivery: a message may be dispatched more than once if a process
///     crashes after dispatch but before the message is marked processed. Notification handlers reached through the
///     outbox should therefore be idempotent. Concurrent double-processing within a single run is prevented by the
///     claim semantics of <see cref="GetPendingAsync" />.
/// </remarks>
public interface IOutboxStore
{
    /// <summary>
    ///     Stores a collection of notifications in the outbox persistence layer.
    ///     This should be executed as part of the same transaction as the business logic.
    /// </summary>
    /// <param name="messages">The outbox messages to store.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task StoreAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken);

    /// <summary>
    ///     Atomically <b>claims</b> a batch of due messages for processing and returns them.
    /// </summary>
    /// <remarks>
    ///     Implementations must transition each returned message from <see cref="OutboxMessageStatus.Pending" /> to
    ///     <see cref="OutboxMessageStatus.InProgress" /> as part of an atomic claim, so that two concurrent processors
    ///     never receive the same message. A message is "due" when its <see cref="OutboxMessage.NextRetryAt" /> is null
    ///     or in the past. Messages should be ordered by <see cref="OutboxMessage.CreatedAt" /> for FIFO processing.
    ///     Implementations may additionally reclaim messages that have been stuck in
    ///     <see cref="OutboxMessageStatus.InProgress" /> beyond a visibility timeout (e.g. after a crash).
    /// </remarks>
    /// <param name="batchSize">The maximum number of messages to claim.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    ///     The claimed messages, now in the <see cref="OutboxMessageStatus.InProgress" /> state, each carrying the
    ///     <see cref="OutboxMessage.Claim" /> that every later operation on it must present.
    /// </returns>
    Task<IEnumerable<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken);

    /// <summary>
    ///     Marks a claimed message as successfully processed.
    /// </summary>
    /// <param name="claim">The claim <see cref="GetPendingAsync" /> issued for the message.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    ///     <c>true</c> when the message was finalized; <c>false</c> when it is unknown, already terminal, or the claim was
    ///     lost (the lease expired and another processor claimed the message). Never throws for those cases.
    /// </returns>
    Task<bool> MarkAsProcessedAsync(OutboxClaim claim, CancellationToken cancellationToken);

    /// <summary>
    ///     Records a failed delivery attempt for a claimed message and returns it for a later retry.
    /// </summary>
    /// <remarks>
    ///     Implementations must increment <see cref="OutboxMessage.AttemptCount" />, store <paramref name="error" />
    ///     in <see cref="OutboxMessage.LastError" />, set <see cref="OutboxMessage.NextRetryAt" /> to
    ///     <paramref name="nextRetryAt" />, and return the message to <see cref="OutboxMessageStatus.Pending" /> so it
    ///     becomes eligible again once the back-off elapses. The persisted attempt count is what lets retry limits
    ///     survive a process restart. A report under a lost claim must change nothing: the message belongs to another
    ///     processor now, and moving it back to pending would put it in two hands at once.
    /// </remarks>
    /// <param name="claim">The claim <see cref="GetPendingAsync" /> issued for the message.</param>
    /// <param name="error">The error message or exception details from the failed attempt.</param>
    /// <param name="nextRetryAt">The earliest UTC time the message may be claimed again, or null for immediately.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    ///     The new <see cref="OutboxMessage.AttemptCount" /> after incrementing; 0 if the message was not found, is
    ///     already terminal, or the claim was lost.
    /// </returns>
    Task<int> IncrementAttemptAsync(OutboxClaim claim, string? error, DateTime? nextRetryAt, CancellationToken cancellationToken);

    /// <summary>
    ///     Permanently marks a claimed message as failed (a dead letter) after its retry attempts were exhausted.
    /// </summary>
    /// <param name="claim">The claim <see cref="GetPendingAsync" /> issued for the message.</param>
    /// <param name="error">The error message or exception details.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><c>true</c> when the message was dead-lettered; <c>false</c> when unknown, already terminal, or the claim was lost.</returns>
    Task<bool> MarkAsFailedAsync(OutboxClaim claim, string? error, CancellationToken cancellationToken);

    /// <summary>
    ///     Extends the lease on a claimed message by the store's visibility timeout, counted from now.
    /// </summary>
    /// <remarks>
    ///     One claim covers a whole batch that is then dispatched message by message, so a slow batch can outlive its
    ///     lease. The processor renews a message's claim before dispatching it once a good part of the lease has elapsed,
    ///     which keeps a second processor from re-delivering the tail of a batch that is still being worked through.
    /// </remarks>
    /// <param name="claim">The claim to extend.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    ///     The claim to use from now on (its token may differ from the one passed in), or <c>null</c> when the claim was
    ///     already lost — the message must then be left alone.
    /// </returns>
    Task<OutboxClaim?> RenewAsync(OutboxClaim claim, CancellationToken cancellationToken);

    /// <summary>
    ///     Gives claimed-but-undispatched messages back, making them immediately claimable again without counting an
    ///     attempt. Used on shutdown, so a restart does not have to wait out the visibility timeout for the rest of the
    ///     batch the stopping processor had claimed.
    /// </summary>
    /// <param name="claims">The claims to release. A lost claim is skipped.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task ReleaseAsync(IReadOnlyCollection<OutboxClaim> claims, CancellationToken cancellationToken);
}
