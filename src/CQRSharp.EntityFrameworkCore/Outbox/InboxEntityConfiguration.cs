using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     The EF Core mapping for <see cref="InboxEntity" />: applied by <c>ApplyCqrsOutbox()</c> together with the outbox
///     table, since the inbox is part of outbox delivery.
/// </summary>
public sealed class InboxEntityConfiguration : IEntityTypeConfiguration<InboxEntity>
{
    /// <summary>Maps <see cref="InboxEntity" />: table, the composite (message, handler) key and the purge index.</summary>
    public void Configure(EntityTypeBuilder<InboxEntity> builder)
    {
        builder.ToTable("CqrsInboxRecords");

        // The composite key is the atomic race point: two recorders of one delivery cannot both insert it.
        builder.HasKey(e => new { e.MessageId, e.HandlerName });
        builder.Property(e => e.HandlerName).HasMaxLength(OutboxEntityConfiguration.HandlerNameMaxLength);
        builder.Property(e => e.DeliveredAt).HasConversion(UtcDateTimeConverters.Instance);

        // Serves the retention purge.
        builder.HasIndex(e => e.DeliveredAt);
    }
}
