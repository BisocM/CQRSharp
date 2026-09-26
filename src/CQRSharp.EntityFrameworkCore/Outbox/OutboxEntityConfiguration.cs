using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     The EF Core mapping for <see cref="OutboxEntity" />. Apply it from your <c>DbContext.OnModelCreating</c> through
///     <c>modelBuilder.ApplyCqrsOutbox()</c> (which also maps the inbox the processor records deliveries in) so the
///     outbox tables are created and indexed alongside your own entities, in the database your unit of work writes to.
/// </summary>
public sealed class OutboxEntityConfiguration : IEntityTypeConfiguration<OutboxEntity>
{
    /// <summary>
    ///     The longest handler name the relational schema accepts (in the outbox and the inbox): it is indexed, so it must
    ///     be bounded. The store rejects a longer one with a clear error rather than letting the provider truncate it or
    ///     fail the command's own transaction.
    /// </summary>
    public const int HandlerNameMaxLength = 256;

    /// <summary>
    ///     The longest partition key the relational schema accepts: it is indexed, so it must be bounded. The store rejects
    ///     a longer one with a clear error rather than letting the provider truncate it or fail the command's own
    ///     transaction.
    /// </summary>
    public const int PartitionKeyMaxLength = 256;

    /// <summary>Maps <see cref="OutboxEntity" />: table, keys, status conversion, concurrency token, UTC coercion, and the claim, partition and retention indexes.</summary>
    public void Configure(EntityTypeBuilder<OutboxEntity> builder)
    {
        builder.ToTable("CqrsOutboxMessages");

        // The database-generated sequence is the primary key: it orders messages with the same CreatedAt by insertion
        // and gives the claim scan a clustered, monotonic key. The caller-supplied message id stays the identity every
        // store operation addresses a message by.
        builder.HasKey(e => e.Sequence);
        builder.Property(e => e.Sequence).ValueGeneratedOnAdd();
        builder.HasAlternateKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.NotificationType).IsRequired();
        builder.Property(e => e.HandlerName).IsRequired().HasMaxLength(HandlerNameMaxLength);
        builder.Property(e => e.PartitionKey).HasMaxLength(PartitionKeyMaxLength);
        builder.Property(e => e.Payload).IsRequired();

        // The status is persisted as its integer ordinal (EF's default for an enum): compact and provider-portable.
        builder.Property(e => e.CreatedAt).HasConversion(UtcDateTimeConverters.Instance);
        builder.Property(e => e.ProcessedAt).HasConversion(UtcDateTimeConverters.Nullable);
        builder.Property(e => e.NextRetryAt).HasConversion(UtcDateTimeConverters.Nullable);
        builder.Property(e => e.FailedAt).HasConversion(UtcDateTimeConverters.Nullable);
        builder.Property(e => e.LockedUntil).HasConversion(UtcDateTimeConverters.Nullable);

        // Optimistic concurrency: a plain integer token the store increments on every mutation, rather than a
        // provider-native rowversion column whose semantics differ across providers (SQL Server: an 8-byte timestamp;
        // SQLite/PostgreSQL: no equivalent). A concurrency token the store writes itself works uniformly everywhere
        // and is all the claim race needs.
        builder.Property(e => e.RowVersion).IsConcurrencyToken();

        // The composite index serving the claim query: filter by Status, then by due-time (NextRetryAt), ordered by
        // CreatedAt then Sequence for FIFO. Covering these in one index keeps the hot claim path off a table scan.
        builder.HasIndex(e => new { e.Status, e.NextRetryAt, e.CreatedAt, e.Sequence });

        // The index serving the partition rule: "is an earlier message with this key and handler still unfinished?"
        builder.HasIndex(e => new { e.PartitionKey, e.HandlerName, e.Status, e.CreatedAt, e.Sequence });

        // The index serving the retention purge of processed messages: a range of the oldest processed rows, deleted a
        // page at a time. Without it every purge reads the whole retained history to find the few rows past retention.
        // Dead letters need none: they are rare, and the claim index already leads with the status.
        builder.HasIndex(e => new { e.Status, e.ProcessedAt });
    }
}
