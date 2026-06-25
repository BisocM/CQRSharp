namespace CQRSharp.Abstractions.Models.Outbox;

/// <summary>
///     Represents a message stored in the outbox for deferred processing.
/// </summary>
/// <param name="Id">The unique identifier for the outbox message.</param>
/// <param name="NotificationType">
///     The stable name of the notification, as produced by <see cref="CQRSharp.Abstractions.Interfaces.Notifications.INotificationSerializer.GetNotificationName" />.
/// </param>
/// <param name="Payload">The serialized content of the notification.</param>
/// <param name="CreatedAt">The UTC timestamp when the message was created.</param>
/// <param name="Status">The current processing status of the message.</param>
/// <param name="ProcessedAt">The UTC timestamp when the message was processed, or null if not yet processed.</param>
/// <param name="LastError">Error information from the most recent failed delivery attempt, if any.</param>
/// <param name="AttemptCount">
///     The number of failed delivery attempts recorded so far. Persisted so that retry limits survive process
///     restarts (rather than being tracked only in memory by the processor).
/// </param>
/// <param name="NextRetryAt">
///     The earliest UTC time at which a failed message becomes eligible to be claimed again, or null to make it
///     immediately eligible. Used to back off between retries.
/// </param>
/// <param name="TraceParent">
///     The W3C <c>traceparent</c> of the request that produced this message, captured at enqueue time so the outbox
///     dispatch span can link back to the originating trace. Null when no trace was active.
/// </param>
public sealed record OutboxMessage(
    Guid Id,
    string NotificationType,
    byte[] Payload,
    DateTime CreatedAt,
    OutboxMessageStatus Status,
    DateTime? ProcessedAt,
    string? LastError,
    int AttemptCount = 0,
    DateTime? NextRetryAt = null,
    string? TraceParent = null
);
