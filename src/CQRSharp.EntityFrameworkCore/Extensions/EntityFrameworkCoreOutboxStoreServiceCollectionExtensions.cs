using System.Diagnostics.CodeAnalysis;
using CQRSharp.Abstractions.Interfaces.Outbox;
using CQRSharp.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

// Namespace-extends the DI builder so registration reads naturally next to the rest of the app's service wiring.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     Registration helpers for the EF Core durable outbox store.
/// </summary>
public static class EntityFrameworkCoreOutboxStoreServiceCollectionExtensions
{
    private const string AotMessage =
        "EF Core uses runtime query compilation and is not compatible with Native AOT or full trimming.";

    /// <summary>
    ///     Registers <see cref="EfCoreOutboxStore{TContext}" /> as the application's <see cref="IOutboxStore" />,
    ///     backed by the already-registered <typeparamref name="TContext" />. This method does NOT register the
    ///     <typeparamref name="TContext" /> itself (call <c>AddDbContext&lt;TContext&gt;</c> yourself) and does NOT
    ///     start the outbox processor (call <c>AddOutboxProcessor</c> separately); it only wires up the store and its
    ///     options.
    /// </summary>
    /// <typeparam name="TContext">The DbContext that maps <c>OutboxEntity</c> (apply <c>OutboxEntityConfiguration</c> in its model).</typeparam>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Optional callback to tune the visibility timeout and concurrency-retry budget.</param>
    /// <returns>The same <paramref name="services" /> for chaining.</returns>
    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    public static IServiceCollection AddEntityFrameworkCoreOutboxStore<TContext>(
        this IServiceCollection services,
        Action<EfCoreOutboxStoreOptions>? configure = null)
        where TContext : DbContext
    {
        var optionsBuilder = services.AddOptions<EfCoreOutboxStoreOptions>();

        if (configure is not null)
            optionsBuilder.Configure(configure);

        optionsBuilder
            .Validate(o => o.VisibilityTimeout > TimeSpan.Zero, "VisibilityTimeout must be greater than zero.")
            .Validate(o => o.MaxClaimAttempts >= 1, "MaxClaimAttempts must be at least 1.")
            .ValidateOnStart();

        // Default the clock to the system provider; tests/callers can replace it before this runs.
        services.TryAddSingleton(TimeProvider.System);

        // Scoped: the store holds a scoped DbContext, so it shares the caller's unit-of-work and transaction.
        services.TryAddScoped<IOutboxStore, EfCoreOutboxStore<TContext>>();

        return services;
    }
}
