using CQRSharp.Abstractions.Interfaces.Outbox;
using CQRSharp.Redis.Outbox;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     Registration helpers for the CQRSharp Redis integration.
/// </summary>
public static class RedisIntegrationServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the Redis-backed durable <see cref="IOutboxStore" />, creating the shared
    ///     <see cref="IConnectionMultiplexer" /> from the given connection string. Does not register the outbox
    ///     processor itself; pair this with the processor registration from the CQRSharp runtime.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The StackExchange.Redis connection string.</param>
    /// <param name="configure">Optional callback to adjust <see cref="RedisOutboxOptions" />.</param>
    /// <returns>The same collection for chaining.</returns>
    public static IServiceCollection AddRedisOutboxStore(
        this IServiceCollection services,
        string connectionString,
        Action<RedisOutboxOptions>? configure = null)
    {
        if (connectionString is null)
            throw new ArgumentNullException(nameof(connectionString));

        services.TryAddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(connectionString));
        return AddCore(services, configure);
    }

    /// <summary>
    ///     Registers the Redis-backed durable <see cref="IOutboxStore" /> against an already-constructed
    ///     <see cref="IConnectionMultiplexer" /> (so an existing Redis connection can be shared). Does not register
    ///     the outbox processor itself; pair this with the processor registration from the CQRSharp runtime.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="multiplexer">The shared Redis connection multiplexer.</param>
    /// <param name="configure">Optional callback to adjust <see cref="RedisOutboxOptions" />.</param>
    /// <returns>The same collection for chaining.</returns>
    public static IServiceCollection AddRedisOutboxStore(
        this IServiceCollection services,
        IConnectionMultiplexer multiplexer,
        Action<RedisOutboxOptions>? configure = null)
    {
        if (multiplexer is null)
            throw new ArgumentNullException(nameof(multiplexer));

        services.TryAddSingleton(multiplexer);
        return AddCore(services, configure);
    }

    // The single shared registration core: validate the options, ensure a TimeProvider, and bind the store.
    private static IServiceCollection AddCore(IServiceCollection services, Action<RedisOutboxOptions>? configure)
    {
        services.AddOptions<RedisOutboxOptions>()
            .Configure(o => configure?.Invoke(o))
            .Validate(o => o.VisibilityTimeout > TimeSpan.Zero, "VisibilityTimeout must be greater than zero.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.KeyPrefix), "KeyPrefix must not be null or whitespace.")
            .Validate(o => o.FinalizedRetention > TimeSpan.Zero, "FinalizedRetention must be greater than zero.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IOutboxStore, RedisOutboxStore>();
        return services;
    }
}
