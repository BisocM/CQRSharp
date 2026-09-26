using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Registries;

/// <summary>
///     Registers the request context factories the source generator discovers, under the factory interface they
///     implement. Called by generated module registrars; public for that reason.
/// </summary>
/// <remarks>
///     One factory serves a context type, and which one is decided by registration order alone: a factory the application
///     registers itself always wins, whether it is registered before or after <c>AddCqrsGenerated</c>; among discovered
///     factories the one registered last wins, which is the composition root's own (its module registers after the
///     modules it references), exactly as its handler wins for a request both it and a library handle. A discovered
///     factory therefore replaces an earlier discovered one, and the built-in factory of <see cref="RequestContextBase" />,
///     but is placed ahead of every registration the application made, so the container, which resolves the last
///     registration of a service, keeps resolving the application's.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class DiscoveredContextFactories
{
    /// <summary>Registers <typeparamref name="TFactory" /> as the discovered factory of <typeparamref name="TContext" />.</summary>
    /// <param name="services">The service collection a module registrar registers into.</param>
    /// <typeparam name="TContext">The context type the factory creates.</typeparam>
    /// <typeparam name="TFactory">The concrete factory type, created as a transient.</typeparam>
    public static void Register<TContext, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TFactory>(
        IServiceCollection services)
        where TContext : IRequestContext
        where TFactory : class, IRequestContextFactory<TContext>
    {
        ArgumentNullException.ThrowIfNull(services);

        var registration = new Discovered(typeof(IRequestContextFactory<TContext>), typeof(TFactory));
        RemoveDiscovered(services, registration.ServiceType);

        // Ahead of the application's own registrations, so the last one - the one the container resolves - stays theirs.
        for (var i = 0; i < services.Count; i++)
            if (IsRegistrationOf(services[i], registration.ServiceType))
            {
                services.Insert(i, registration);
                return;
            }

        services.Add(registration);
    }

    /// <summary>
    ///     Registers the built-in factory of <see cref="RequestContextBase" /> unless a factory for it is registered
    ///     already, discovered or the application's own. A factory discovered later replaces it.
    /// </summary>
    internal static void RegisterDefault(IServiceCollection services)
    {
        var serviceType = typeof(IRequestContextFactory<RequestContextBase>);
        foreach (var descriptor in services)
            if (IsRegistrationOf(descriptor, serviceType))
                return;

        services.Add(new Discovered(serviceType, static sp => new DefaultRequestContextFactory(sp.GetRequiredService<TimeProvider>())));
    }

    private static void RemoveDiscovered(IServiceCollection services, Type serviceType)
    {
        for (var i = services.Count - 1; i >= 0; i--)
            if (services[i] is Discovered && services[i].ServiceType == serviceType)
                services.RemoveAt(i);
    }

    private static bool IsRegistrationOf(ServiceDescriptor descriptor, Type serviceType)
        => descriptor.ServiceType == serviceType && !descriptor.IsKeyedService;

    // The registrations made here are told apart from the application's by their type: nothing else creates one.
    private sealed class Discovered : ServiceDescriptor
    {
        public Discovered(Type serviceType, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type factoryType)
            : base(serviceType, factoryType, ServiceLifetime.Transient)
        {
        }

        public Discovered(Type serviceType, Func<IServiceProvider, object> factory)
            : base(serviceType, factory, ServiceLifetime.Transient)
        {
        }
    }
}
