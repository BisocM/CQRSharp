using System.Diagnostics.CodeAnalysis;
using CQRSharp.Core.Extensions;
using CQRSharp.EntityFrameworkCore;
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
    private const string AotMessage =
        "EF Core uses runtime query compilation and is not compatible with Native AOT or full trimming.";

    /// <summary>
    ///     Uses the EF Core durable outbox store, backed by the already-registered <typeparamref name="TContext" />.
    /// </summary>
    /// <typeparam name="TContext">The DbContext that maps <c>OutboxEntity</c> (apply <c>OutboxEntityConfiguration</c> in its model).</typeparam>
    /// <param name="builder">The outbox store builder.</param>
    /// <param name="configure">Optional callback to tune the visibility timeout and concurrency-retry budget.</param>
    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
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
    /// <typeparam name="TContext">The DbContext that maps <c>IdempotencyEntity</c> (apply <c>IdempotencyEntityConfiguration</c> in its model).</typeparam>
    /// <param name="builder">The idempotency store builder.</param>
    /// <param name="configure">Optional callback to tune the retention window.</param>
    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    public static IdempotencyStoreBuilder UseEntityFrameworkCore<TContext>(
        this IdempotencyStoreBuilder builder,
        Action<EfCoreIdempotencyStoreOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseStore(services => services.AddEntityFrameworkCoreIdempotencyStore<TContext>(configure));
    }
}
