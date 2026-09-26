using CQRSharp.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     What each outbox retention rule deletes, oldest first by the column the rule measures, so a page of it is a range
///     of the index that serves the rule.
/// </summary>
internal static class OutboxRetentionRules
{
    /// <summary>Processed messages processed at or before <paramref name="processedBefore" />; served by the (Status, ProcessedAt) index.</summary>
    public static IOrderedQueryable<OutboxEntity> ProcessedBy(DbContext context, DateTime processedBefore)
        => context.Set<OutboxEntity>()
            .Where(e => e.Status == OutboxMessageStatus.Processed && e.ProcessedAt != null && e.ProcessedAt <= processedBefore)
            .OrderBy(e => e.ProcessedAt);

    /// <summary>
    ///     Dead letters that failed at or before <paramref name="failedBefore" />: what an explicit purge and the dead-letter
    ///     retention delete. A dead letter without a failure time was dead-lettered before the outbox table had the column,
    ///     and cannot be delivered any more (it carries no handler name), so it counts as older than any cut-off.
    /// </summary>
    public static IOrderedQueryable<OutboxEntity> DeadLettersFailedBy(DbContext context, DateTime failedBefore)
        => context.Set<OutboxEntity>()
            .Where(e => e.Status == OutboxMessageStatus.Failed && (e.FailedAt == null || e.FailedAt <= failedBefore))
            .OrderBy(e => e.Sequence);

    /// <summary>Inbox records written at or before <paramref name="deliveredBefore" />; served by the DeliveredAt index.</summary>
    public static IOrderedQueryable<InboxEntity> InboxRecordsBy(DbContext context, DateTime deliveredBefore)
        => context.Set<InboxEntity>()
            .Where(e => e.DeliveredAt <= deliveredBefore)
            .OrderBy(e => e.DeliveredAt);
}
