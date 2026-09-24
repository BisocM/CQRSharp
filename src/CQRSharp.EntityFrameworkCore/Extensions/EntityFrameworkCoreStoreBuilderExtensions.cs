using System.Diagnostics.CodeAnalysis;
using CQRSharp.EntityFrameworkCore;
using CQRSharp.Pipelines;
using Microsoft.EntityFrameworkCore;

// Namespace-extends the DI builder so the fluent store verbs read naturally next to the rest of the app's wiring.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     Fluent store verbs for the EF Core integration, used inside the builder's <c>UseOutbox(...)</c> and
///     <c>UseIdempotency(...)</c> so the EF Core store can be selected in the same call. Backs the already-registered
///     <c>DbContext</c> (register it with <c>AddDbContext&lt;TContext&gt;</c> yourself and apply the entity
///     configurations in its model). EF Core is not Native-AOT/full-trim compatible — these verbs carry the
///     corresponding analyzer annotations.
/// </summary>
public static class EntityFrameworkCoreStoreBuilderExtensions
{

    /// <summary>
    ///     Uses the EF Core durable outbox store, backed by the already-registered <typeparamref name="TContext" />.
    /// </summary>
    /// <typeparam name="TContext">The DbContext that maps the outbox and inbox tables (call <c>modelBuilder.ApplyCqrsOutbox()</c> in its model); host start fails when it does not.</typeparam>
    /// <param name="builder">The outbox store builder.</param>
    /// <param name="configure">Optional callback to tune the visibility timeout, the claim's retry budget and the retention.</param>
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public static OutboxStoreBuilder UseEntityFrameworkCore<TContext>(
        this OutboxStoreBuilder builder,
        Action<EfCoreOutboxStoreOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseStore(services => services.AddEntityFrameworkCoreOutboxStore<TContext>(configure));
    }

    /// <summary>
    ///     Uses the EF Core durable idempotency store, backed by the already-registered <typeparamref name="TContext" />.
    /// </summary>
    /// <typeparam name="TContext">
    ///     The DbContext that maps <c>IdempotencyEntity</c> (call <c>modelBuilder.ApplyCqrsIdempotency(...)</c> in its model,
    ///     with <see cref="IdempotencyEntityConfiguration.SqlServerBinaryCollation" /> on SQL Server); host start fails when
    ///     it does not.
    /// </typeparam>
    /// <param name="builder">The idempotency store builder.</param>
    /// <param name="configure">Optional callback to tune the retention window.</param>
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public static IdempotencyStoreBuilder UseEntityFrameworkCore<TContext>(
        this IdempotencyStoreBuilder builder,
        Action<EfCoreIdempotencyStoreOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseStore(services => services.AddEntityFrameworkCoreIdempotencyStore<TContext>(configure));
    }
}
