using CQRSharp.Persistence;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>Maps between the store's <see cref="OutboxMessage" /> and the persisted <see cref="OutboxEntity" />.</summary>
internal static class OutboxEntityMapper
{
    /// <summary>The message a persisted row represents; a claim the store took on it travels beside it.</summary>
    public static OutboxMessage ToMessage(OutboxEntity entity) => new(
        entity.Id,
        entity.NotificationType,
        entity.HandlerName,
        entity.Payload,
        entity.CreatedAt,
        entity.Status,
        entity.ProcessedAt,
        entity.LastError,
        entity.AttemptCount,
        entity.NextRetryAt,
        entity.TraceParent,
        entity.PartitionKey,
        entity.NotificationId,
        entity.FailedAt);

    /// <summary>The row a new message is stored as.</summary>
    public static OutboxEntity FromMessage(OutboxMessage message)
    {
        // The relational columns are bounded; a value the provider would truncate (or reject inside the command's own
        // transaction, on providers that check) is refused here, with the cause named.
        if (message.HandlerName.Length > OutboxEntityConfiguration.HandlerNameMaxLength)
            throw new InvalidOperationException(
                $"The handler name '{message.HandlerName}' is longer than the {OutboxEntityConfiguration.HandlerNameMaxLength} characters the EF Core outbox stores; pin a shorter name with [NotificationHandlerName].");
        if (message.PartitionKey is { Length: > OutboxEntityConfiguration.PartitionKeyMaxLength })
            throw new InvalidOperationException(
                $"The partition key of '{message.NotificationType}' is longer than the {OutboxEntityConfiguration.PartitionKeyMaxLength} characters the EF Core outbox stores; derive a shorter key (a hash of the natural one, for instance).");

        return new OutboxEntity
        {
            Id = message.Id,
            NotificationId = message.NotificationId,
            NotificationType = message.NotificationType,
            HandlerName = message.HandlerName,
            PartitionKey = message.PartitionKey,
            Payload = message.Payload,
            CreatedAt = message.CreatedAt,
            Status = message.Status,
            ProcessedAt = message.ProcessedAt,
            LastError = message.LastError,
            AttemptCount = message.AttemptCount,
            NextRetryAt = message.NextRetryAt,
            FailedAt = message.FailedAt,
            TraceParent = message.TraceParent
        };
    }
}
