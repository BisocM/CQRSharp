using CQRSharp.Abstractions.Models.Outbox;

namespace CQRSharp.Abstractions.Interfaces.Outbox;

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
    /// <returns>The claimed messages, now in the <see cref="OutboxMessageStatus.InProgress" /> state.</returns>
    Task<IEnumerable<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken);

    /// <summary>
    ///     Marks a claimed message as successfully processed.
    /// </summary>
    /// <param name="messageId">The unique identifier of the message to mark.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task MarkAsProcessedAsync(Guid messageId, CancellationToken cancellationToken);

    /// <summary>
    ///     Records a failed delivery attempt for a claimed message and returns it for a later retry.
    /// </summary>
    /// <remarks>
    ///     Implementations must increment <see cref="OutboxMessage.AttemptCount" />, store <paramref name="error" />
    ///     in <see cref="OutboxMessage.LastError" />, set <see cref="OutboxMessage.NextRetryAt" /> to
    ///     <paramref name="nextRetryAt" />, and return the message to <see cref="OutboxMessageStatus.Pending" /> so it
    ///     becomes eligible again once the back-off elapses. The persisted attempt count is what lets retry limits
    ///     survive a process restart.
    /// </remarks>
    /// <param name="messageId">The unique identifier of the message.</param>
    /// <param name="error">The error message or exception details from the failed attempt.</param>
    /// <param name="nextRetryAt">The earliest UTC time the message may be claimed again, or null for immediately.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The new <see cref="OutboxMessage.AttemptCount" /> after incrementing; 0 if the message was not found.</returns>
    Task<int> IncrementAttemptAsync(Guid messageId, string? error, DateTime? nextRetryAt, CancellationToken cancellationToken);

    /// <summary>
    ///     Permanently marks a message as failed (a dead letter) after its retry attempts were exhausted.
    /// </summary>
    /// <param name="messageId">The unique identifier of the message to mark.</param>
    /// <param name="error">The error message or exception details.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task MarkAsFailedAsync(Guid messageId, string? error, CancellationToken cancellationToken);
}