namespace CQRSharp.Abstractions.Data.Models.Outbox;

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
    Failed
}