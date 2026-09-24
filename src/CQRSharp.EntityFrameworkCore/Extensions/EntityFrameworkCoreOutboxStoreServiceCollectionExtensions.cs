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
///     Registration helpers for the EF Core durable outbox store.
/// </summary>
public static class EntityFrameworkCoreOutboxStoreServiceCollectionExtensions
{

    /// <summary>
    ///     Registers the EF Core outbox store as the application's <see cref="IOutboxStore" /> and the EF Core inbox as its
    ///     <see cref="IInboxStore" />, both backed by the already-registered <typeparamref name="TContext" />,
    ///     <b>replacing</b> the outbox and inbox stores already registered (the last explicit store registration wins,
    ///     whatever the order relative to <c>AddCqrsGenerated</c>), together with the hosted service that purges them
    ///     (<see cref="EfCoreOutboxStoreOptions.ProcessedRetention" />, <see cref="EfCoreOutboxStoreOptions.DeadLetterRetention" />,
    ///     <see cref="EfCoreOutboxStoreOptions.InboxRetention" />). This method does NOT register the
    ///     <typeparamref name="TContext" /> itself (call <c>AddDbContext&lt;TContext&gt;</c> yourself). The outbox
    ///     processor is always registered by <c>AddCqrsGenerated</c>; the store is used once the outbox is on
    ///     (<c>UseOutbox(...)</c>, or <c>OutboxOptions.Mode</c>). Prefer
    ///     <c>UseOutbox(o =&gt; o.UseEntityFrameworkCore&lt;TContext&gt;())</c>, which does both in one step.
    /// </summary>
    /// <typeparam name="TContext">The DbContext that maps the outbox and inbox tables (call <c>modelBuilder.ApplyCqrsOutbox()</c> in its model); host start fails when it does not.</typeparam>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Optional callback to tune the visibility timeout, the claim's retry budget and the retention.</param>
    /// <returns>The same <paramref name="services" /> for chaining.</returns>
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public static IServiceCollection AddEntityFrameworkCoreOutboxStore<TContext>(
        this IServiceCollection services,
        Action<EfCoreOutboxStoreOptions>? configure = null)
        where TContext : DbContext
    {
        var optionsBuilder = services.AddOptions<EfCoreOutboxStoreOptions>();
        if (configure is not null)
            optionsBuilder.Configure(configure);
        optionsBuilder.ValidateOnStart();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<EfCoreOutboxStoreOptions>, EfCoreOutboxStoreOptionsValidator>());
        // The model is checked at host start too: a context that maps only the outbox would dead-letter every message.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<EfCoreOutboxStoreOptions>, EfCoreOutboxModelValidator<TContext>>());

        // Default the clock to the system provider; tests/callers can replace it before this runs.
        services.TryAddSingleton(TimeProvider.System);

        // Scoped: the store holds a scoped DbContext, so it shares the caller's unit-of-work and transaction. Swapped as
        // a pair with the inbox, so no other store's inbox is left recording this store's deliveries.
        services.RemoveAll<IOutboxStore>();
        services.RemoveAll<IInboxStore>();
        services.AddScoped<IOutboxStore, EfCoreOutboxStore<TContext>>();
        // The inbox shares the scope's context, so a delivery record commits with the handler's own changes.
        services.AddScoped<IInboxStore, EfCoreInboxStore<TContext>>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, EfCoreOutboxRetention<TContext>>());
        // How the retention and the model check learn whether this store is still the one registered.
        services.TryAddSingleton(new EfCoreStoreSelection(services));

        return services;
    }
}
