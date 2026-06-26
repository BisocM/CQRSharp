using CQRSharp.Abstractions.Models.Outbox;

namespace CQRSharp.EntityFrameworkCore.Persistence;

/// <summary>
///     The single source of truth for translating between the persisted <see cref="OutboxEntity" /> row and the
///     immutable public <see cref="OutboxMessage" /> transport record. Centralizing the field-by-field mapping here
///     keeps the store free of duplicated copy logic and guarantees the two directions stay in sync.
/// </summary>
internal static class OutboxEntityMapper
{
    /// <summary>Projects a persisted row to the public message record (lease/concurrency fields are store-internal and dropped).</summary>
    public static OutboxMessage ToMessage(OutboxEntity entity) => new(
        entity.Id,
        entity.NotificationType,
        entity.Payload,
        entity.CreatedAt,
        entity.Status,
        entity.ProcessedAt,
        entity.LastError,
        entity.AttemptCount,
        entity.NextRetryAt,
        entity.TraceParent);

    /// <summary>Copies the message's transport fields onto an existing tracked row, leaving lease/concurrency state untouched.</summary>
    public static void Apply(OutboxEntity entity, OutboxMessage message)
    {
        entity.NotificationType = message.NotificationType;
        entity.Payload = message.Payload;
        entity.CreatedAt = message.CreatedAt;
        entity.Status = message.Status;
        entity.ProcessedAt = message.ProcessedAt;
        entity.LastError = message.LastError;
        entity.AttemptCount = message.AttemptCount;
        entity.NextRetryAt = message.NextRetryAt;
        entity.TraceParent = message.TraceParent;
    }

    /// <summary>Builds a fresh row from a message; the row starts unleased with a zero concurrency token.</summary>
    public static OutboxEntity FromMessage(OutboxMessage message)
    {
        var entity = new OutboxEntity { Id = message.Id };
        Apply(entity, message);
        return entity;
    }
}
