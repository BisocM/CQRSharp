namespace CQRSharp.Abstractions.Data.Models.Outbox;

/// <summary>
///     Represents a message stored in the outbox for deferred processing.
/// </summary>
/// <param name="Id">The unique identifier for the outbox message.</param>
/// <param name="NotificationType">
///     The stable name of the notification, as produced by <see cref="CQRSharp.Abstractions.Data.Interfaces.Notifications.INotificationSerializer.GetNotificationName" />.
/// </param>
/// <param name="Payload">The serialized content of the notification.</param>
/// <param name="CreatedAt">The UTC timestamp when the message was created.</param>
/// <param name="Status">The current processing status of the message.</param>
/// <param name="ProcessedAt">The UTC timestamp when the message was processed, or null if not yet processed.</param>
/// <param name="Error">Error information if processing failed.</param>
public sealed record OutboxMessage(
    Guid Id,
    string NotificationType,
    byte[] Payload,
    DateTime CreatedAt,
    OutboxMessageStatus Status,
    DateTime? ProcessedAt,
    string? Error
);
