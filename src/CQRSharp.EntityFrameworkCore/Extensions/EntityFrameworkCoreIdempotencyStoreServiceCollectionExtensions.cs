using System.Diagnostics.CodeAnalysis;
using CQRSharp.EntityFrameworkCore;
using CQRSharp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
// Namespace-extends the DI builder so registration reads naturally next to the rest of the app's service wiring.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     Registration helpers for the EF Core durable idempotency store.
/// </summary>
public static class EntityFrameworkCoreIdempotencyStoreServiceCollectionExtensions
{

    /// <summary>
    ///     Registers the EF Core idempotency store as the application's <see cref="IIdempotencyStore" />, backed by the
    ///     already-registered <typeparamref name="TContext" />, <b>replacing</b> the idempotency store already registered
    ///     (the last explicit store registration wins, whatever the order relative to <c>AddCqrsGenerated</c>), together
    ///     with the hosted service that deletes expired keys. The store opens a scope, and so a fresh
    ///     <typeparamref name="TContext" />, per operation. This method does NOT register the <typeparamref name="TContext" />
    ///     itself (call <c>AddDbContext&lt;TContext&gt;</c> yourself), and does not turn on the idempotency behavior:
    ///     prefer <c>UseIdempotency(i =&gt; i.UseEntityFrameworkCore&lt;TContext&gt;())</c>, which does both in one step.
    /// </summary>
    /// <typeparam name="TContext">
    ///     The DbContext that maps <c>IdempotencyEntity</c> (call <c>modelBuilder.ApplyCqrsIdempotency(...)</c> in its model,
    ///     with <see cref="IdempotencyEntityConfiguration.SqlServerBinaryCollation" /> on SQL Server); host start fails when
    ///     it does not.
    /// </typeparam>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Optional callback to tune the retention window.</param>
    /// <returns>The same <paramref name="services" /> for chaining.</returns>
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public static IServiceCollection AddEntityFrameworkCoreIdempotencyStore<TContext>(
        this IServiceCollection services,
        Action<EfCoreIdempotencyStoreOptions>? configure = null)
        where TContext : DbContext
    {
        var optionsBuilder = services.AddOptions<EfCoreIdempotencyStoreOptions>();
        if (configure is not null)
            optionsBuilder.Configure(configure);
        optionsBuilder.ValidateOnStart();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<EfCoreIdempotencyStoreOptions>, EfCoreIdempotencyStoreOptionsValidator>());
        // The context must map the table, with a case-sensitive key column where the provider's default folds case.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<EfCoreIdempotencyStoreOptions>, EfCoreIdempotencyModelValidator<TContext>>());

        // Default the clock to the system provider; tests/callers can replace it before this runs.
        services.TryAddSingleton(TimeProvider.System);

        // A singleton that resolves a fresh TContext per operation through a scope: injecting the (scoped) TContext
        // directly would make it a captive dependency of this singleton.
        services.RemoveAll<IIdempotencyStore>();
        services.AddSingleton<IIdempotencyStore, EfCoreIdempotencyStore<TContext>>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, EfCoreIdempotencyRetention<TContext>>());
        // How the retention and the model check learn whether this store is still the one registered.
        services.TryAddSingleton(new EfCoreStoreSelection(services));

        return services;
    }
}
