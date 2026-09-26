using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace CQRSharp.Redis;

/// <summary>
///     Turns what a store registration was given into the one shape the registration cores take: a function that yields
///     the store's connection from the service provider. The stores never resolve an <see cref="IConnectionMultiplexer" />
///     from the container, so each runs on exactly the connection it was registered with, whatever else the application
///     registers.
/// </summary>
internal static class RedisConnectionSource
{
    /// <summary>A connection the application passed in: used as given, and never disposed by CQRSharp.</summary>
    public static Func<IServiceProvider, IConnectionMultiplexer> Given(IConnectionMultiplexer multiplexer)
        => _ => multiplexer;

    /// <summary>
    ///     A connection opened from <paramref name="connectionString" /> through the provider's
    ///     <see cref="RedisConnectionPool" />, which shares it with every store given the same string and disposes it with
    ///     the provider.
    /// </summary>
    public static Func<IServiceProvider, IConnectionMultiplexer> Pooled(IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton<RedisConnectionPool>();
        return provider => provider.GetRequiredService<RedisConnectionPool>().Get(connectionString);
    }

    /// <summary>A connection the application's factory supplies; a factory that returns null fails the resolution, naming the registration.</summary>
    public static Func<IServiceProvider, IConnectionMultiplexer> FromFactory(Func<IServiceProvider, IConnectionMultiplexer> factory, string registration)
        => provider => factory(provider)
            ?? throw new InvalidOperationException($"The connection factory passed to {registration} returned null.");
}
