using System.Diagnostics.CodeAnalysis;
using CQRSharp.Pipelines;
using CQRSharp.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Namespace-extends the DI builder so registration reads naturally next to the rest of the app's service wiring.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     Registration helpers for the EF Core durable idempotency store.
/// </summary>
public static class EntityFrameworkCoreIdempotencyStoreServiceCollectionExtensions
{
    private const string AotMessage =
        "EF Core uses runtime query compilation and is not compatible with Native AOT or full trimming.";

    /// <summary>
    ///     Registers <see cref="EfCoreIdempotencyStore{TContext}" /> as the application's <see cref="IIdempotencyStore" />,
    ///     backed by the already-registered <typeparamref name="TContext" />. This method does NOT register the
    ///     <typeparamref name="TContext" /> itself (call <c>AddDbContext&lt;TContext&gt;</c> yourself); it only wires up
    ///     the store and its options.
    /// </summary>
    /// <typeparam name="TContext">The DbContext that maps <c>IdempotencyEntity</c> (apply <c>IdempotencyEntityConfiguration</c> in its model).</typeparam>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Optional callback to tune the retention window.</param>
    /// <returns>The same <paramref name="services" /> for chaining.</returns>
    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    public static IServiceCollection AddEntityFrameworkCoreIdempotencyStore<TContext>(
        this IServiceCollection services,
        Action<EfCoreIdempotencyStoreOptions>? configure = null)
        where TContext : DbContext
    {
        var optionsBuilder = services.AddOptions<EfCoreIdempotencyStoreOptions>();

        if (configure is not null)
            optionsBuilder.Configure(configure);

        optionsBuilder
            .Validate(o => o.Retention > TimeSpan.Zero, "Retention must be greater than zero.")
            .ValidateOnStart();

        // Default the clock to the system provider; tests/callers can replace it before this runs.
        services.TryAddSingleton(TimeProvider.System);

        // A singleton that resolves a fresh TContext per operation through a scope: injecting the (scoped) TContext
        // directly would make it a captive dependency of this singleton. The factory pins the scope-factory constructor.
        services.TryAddSingleton<IIdempotencyStore>(sp => new EfCoreIdempotencyStore<TContext>(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptions<EfCoreIdempotencyStoreOptions>>(),
            sp.GetRequiredService<ILogger<EfCoreIdempotencyStore<TContext>>>()));

        return services;
    }
}
