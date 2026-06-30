using CQRSharp.EntityFrameworkCore.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CQRSharp.EntityFrameworkCore.Extensions;

/// <summary>
///     <see cref="ModelBuilder" /> helpers that apply the CQRSharp EF Core entity mappings in one call, so a consumer's
///     <c>OnModelCreating</c> does not have to construct each <c>IEntityTypeConfiguration</c> by hand. Registering the
///     store via <c>UseEntityFrameworkCore&lt;TContext&gt;()</c> wires the store but NOT the model — you still apply the
///     mapping here and add a migration. These helpers make that step a single, discoverable call.
/// </summary>
public static class CqrsModelBuilderExtensions
{
    /// <summary>
    ///     Applies the outbox table mapping (<see cref="OutboxEntityConfiguration" />). Call this from your
    ///     <c>DbContext.OnModelCreating</c> when you use <c>UseEntityFrameworkCore&lt;TContext&gt;()</c> for the outbox
    ///     store, then add a migration so the <c>CqrsOutboxMessages</c> table exists. Without it the processor's first
    ///     poll fails with "no such table".
    /// </summary>
    public static ModelBuilder ApplyCqrsOutbox(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfiguration(new OutboxEntityConfiguration());
        return modelBuilder;
    }

    /// <summary>
    ///     Applies the idempotency table mapping (<see cref="IdempotencyEntityConfiguration" />). Call this from your
    ///     <c>DbContext.OnModelCreating</c> when you use the EF Core idempotency store, then add a migration so the
    ///     <c>CqrsIdempotencyKeys</c> table exists.
    /// </summary>
    public static ModelBuilder ApplyCqrsIdempotency(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfiguration(new IdempotencyEntityConfiguration());
        return modelBuilder;
    }
}
