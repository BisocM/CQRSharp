namespace CQRSharp.Abstractions.Models.Outbox;

/// <summary>
///     Represents the processing status of an <see cref="OutboxMessage" />.
/// </summary>
public enum OutboxMessageStatus
{
    /// <summary>
    ///     The message is pending and waiting to be processed.
    /// </summary>
    Pending,

    /// <summary>
    ///     The message has been successfully processed.
    /// </summary>
    Processed,

    /// <summary>
    ///     The message failed to be processed after multiple attempts.
    /// </summary>
    Failed,

    /// <summary>
    ///     The message has been claimed by a processor and is currently being dispatched.
    ///     A store should only hand a <see cref="Pending" /> message to one processor at a time by transitioning it
    ///     to this state atomically (see <see cref="CQRSharp.Abstractions.Interfaces.Outbox.IOutboxStore.GetPendingAsync" />).
    ///     Appended last so the ordinal values of the original members remain stable for any persisted data.
    /// </summary>
    InProgress
}