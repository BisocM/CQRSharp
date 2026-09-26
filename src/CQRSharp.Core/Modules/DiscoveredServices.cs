using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Modules;

/// <summary>
///     The validators, exception hooks and closed pipeline behaviors a generated module discovers are registered under
///     <see cref="Key" />, not in the application's own registrations of their interface, and merged with those where
///     they are consumed: a discovered implementation is used unless the application registered an implementation of
///     the same type for the interface itself. A registration the application makes before or after
///     <c>AddCqrsGenerated</c> therefore never runs a discovered validator, hook or behavior twice, and the application's
///     own registration of it is the one used.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class DiscoveredServices
{
    /// <summary>
    ///     The service key discovered validators, exception hooks and closed pipeline behaviors are registered under.
    ///     Generated code compiles the value in, so it is part of the contract between compiled modules and the runtime
    ///     for the major version.
    /// </summary>
    public const string Key = "CQRSharp.Discovered";

    /// <summary>The discovered and the registered services of <typeparamref name="TService" />, merged (see <see cref="Merge{TService}" />).</summary>
    internal static IReadOnlyList<TService> Resolve<TService>(IServiceProvider services) where TService : class
        => Merge(services.GetKeyedServices<TService>(Key), services.GetServices<TService>());

    /// <summary>
    ///     Every <paramref name="discovered" /> service whose implementation type none of the <paramref name="registered" />
    ///     ones has, in discovery order, then every registered one, in registration order.
    /// </summary>
    internal static IReadOnlyList<TService> Merge<TService>(IEnumerable<TService> discovered, IEnumerable<TService> registered)
        where TService : class
    {
        var discoveredServices = discovered as TService[] ?? discovered.ToArray();
        var registeredServices = registered as TService[] ?? registered.ToArray();
        if (discoveredServices.Length == 0) return registeredServices;
        if (registeredServices.Length == 0) return discoveredServices;

        var registeredTypes = new HashSet<Type>();
        foreach (var service in registeredServices)
            registeredTypes.Add(service.GetType());

        var merged = new List<TService>(discoveredServices.Length + registeredServices.Length);
        foreach (var service in discoveredServices)
            if (!registeredTypes.Contains(service.GetType()))
                merged.Add(service);
        merged.AddRange(registeredServices);
        return merged;
    }
}
