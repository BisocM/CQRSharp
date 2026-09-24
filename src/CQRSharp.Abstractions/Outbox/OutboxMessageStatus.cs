namespace CQRSharp.Persistence;

/// <summary>
///     Represents the processing status of an <see cref="OutboxMessage" />.
/// </summary>
public enum OutboxMessageStatus
{
    /// <summary>
    ///     The message waits to be delivered: due now, or backing off until <see cref="OutboxMessage.NextRetryAt" />.
    /// </summary>
    Pending,

    /// <summary>
    ///     The message has been successfully processed.
    /// </summary>
    Processed,

    /// <summary>
    ///     The message is dead-lettered: its delivery attempts ran out, its payload cannot be read, or its notification
    ///     or handler stayed unknown past the unknown-recipient grace period. It stays until it is requeued or purged.
    /// </summary>
    Failed,

    /// <summary>
    ///     The message has been claimed by a processor and is currently being dispatched.
    ///     A store should only hand a <see cref="Pending" /> message to one processor at a time by transitioning it
    ///     to this state atomically (see <see cref="CQRSharp.Persistence.IOutboxStore.ClaimPendingAsync" />). Stores
    ///     may persist the status as its ordinal, so the members keep their values.
    /// </summary>
    InProgress
}
