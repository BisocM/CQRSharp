using CQRSharp.Abstractions.Data.Models.Outbox;

namespace CQRSharp.Abstractions.Data.Interfaces.Outbox;

/// <summary>
///     Defines the contract for a persistence store for outbox messages.
///     Implement this interface to provide a storage mechanism (e.g., using EF Core, Dapper) for notifications
///     that are processed via the outbox pattern. The implementation should ensure that storing messages
///     is atomic with the primary business transaction.
/// </summary>
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
    ///     Retrieves a batch of pending messages from the outbox store for processing.
    ///     Implementations should order messages by their creation date to ensure FIFO processing.
    /// </summary>
    /// <param name="batchSize">The maximum number of messages to retrieve.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A collection of pending outbox messages.</returns>
    Task<IEnumerable<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken);

    /// <summary>
    ///     Marks a message as successfully processed.
    /// </summary>
    /// <param name="messageId">The unique identifier of the message to mark.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task MarkAsProcessedAsync(Guid messageId, CancellationToken cancellationToken);

    /// <summary>
    ///     Marks a message as failed after processing attempts were exhausted.
    /// </summary>
    /// <param name="messageId">The unique identifier of the message to mark.</param>
    /// <param name="error">The error message or exception details.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task MarkAsFailedAsync(Guid messageId, string? error, CancellationToken cancellationToken);
}