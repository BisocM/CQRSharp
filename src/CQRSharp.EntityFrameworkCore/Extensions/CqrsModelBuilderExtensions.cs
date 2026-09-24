using Microsoft.EntityFrameworkCore;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     <see cref="ModelBuilder" /> helpers that apply the CQRSharp EF Core entity mappings in one call, so a consumer's
///     <c>OnModelCreating</c> does not have to construct each <c>IEntityTypeConfiguration</c> by hand. Registering the
///     store via <c>UseEntityFrameworkCore&lt;TContext&gt;()</c> wires the store but NOT the model — you still apply the
///     mapping here and add a migration. These helpers make that step a single, discoverable call.
/// </summary>
public static class CqrsModelBuilderExtensions
{
    /// <summary>
    ///     Applies the outbox table mappings (<see cref="OutboxEntityConfiguration" /> and, for the inbox that records
    ///     completed deliveries, <see cref="InboxEntityConfiguration" />). Call this from your
    ///     <c>DbContext.OnModelCreating</c> when you use <c>UseEntityFrameworkCore&lt;TContext&gt;()</c> for the outbox
    ///     store, then add a migration so the <c>CqrsOutboxMessages</c> and <c>CqrsInboxRecords</c> tables exist. A context
    ///     that does not map them fails host start; one whose database lacks the tables fails the processor's first poll.
    /// </summary>
    public static ModelBuilder ApplyCqrsOutbox(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfiguration(new OutboxEntityConfiguration());
        modelBuilder.ApplyConfiguration(new InboxEntityConfiguration());
        return modelBuilder;
    }

    /// <summary>
    ///     Applies the idempotency table mapping (<see cref="IdempotencyEntityConfiguration" />). Call this from your
    ///     <c>DbContext.OnModelCreating</c> when you use the EF Core idempotency store, then add a migration so the
    ///     <c>CqrsIdempotencyKeys</c> table exists.
    /// </summary>
    /// <param name="modelBuilder">The model builder of the context that holds the idempotency table.</param>
    /// <param name="keyCollation">
    ///     The key column's collation, or <c>null</c> for the database's default. Keys are compared ordinally and
    ///     case-sensitively: on SQL Server pass <see cref="IdempotencyEntityConfiguration.SqlServerBinaryCollation" />
    ///     (host start fails without a case-sensitive one); SQLite and PostgreSQL need none.
    /// </param>
    public static ModelBuilder ApplyCqrsIdempotency(this ModelBuilder modelBuilder, string? keyCollation = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfiguration(new IdempotencyEntityConfiguration(keyCollation));
        return modelBuilder;
    }
}
