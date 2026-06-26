using CQRSharp.Abstractions.Interfaces.Idempotency;
using CQRSharp.Redis.Idempotency;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     Registration helpers for the CQRSharp Redis-backed idempotency store.
/// </summary>
public static class RedisIdempotencyServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the Redis-backed durable <see cref="IIdempotencyStore" />, creating the shared
    ///     <see cref="IConnectionMultiplexer" /> from the given connection string. The multiplexer is built lazily so
    ///     nothing connects to Redis at registration time.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The StackExchange.Redis connection string.</param>
    /// <param name="configure">Optional callback to adjust <see cref="RedisIdempotencyOptions" />.</param>
    /// <returns>The same collection for chaining.</returns>
    public static IServiceCollection AddRedisIdempotencyStore(
        this IServiceCollection services,
        string connectionString,
        Action<RedisIdempotencyOptions>? configure = null)
    {
        if (connectionString is null)
            throw new ArgumentNullException(nameof(connectionString));

        // TryAddSingleton with a factory means the multiplexer is only constructed on first resolution, so nothing
        // connects to Redis during registration; shared with any other CQRSharp Redis integration in the container.
        services.TryAddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(connectionString));
        return AddCore(services, configure);
    }

    /// <summary>
    ///     Registers the Redis-backed durable <see cref="IIdempotencyStore" /> against an already-constructed
    ///     <see cref="IConnectionMultiplexer" /> (so an existing Redis connection can be shared).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="multiplexer">The shared Redis connection multiplexer.</param>
    /// <param name="configure">Optional callback to adjust <see cref="RedisIdempotencyOptions" />.</param>
    /// <returns>The same collection for chaining.</returns>
    public static IServiceCollection AddRedisIdempotencyStore(
        this IServiceCollection services,
        IConnectionMultiplexer multiplexer,
        Action<RedisIdempotencyOptions>? configure = null)
    {
        if (multiplexer is null)
            throw new ArgumentNullException(nameof(multiplexer));

        services.TryAddSingleton(multiplexer);
        return AddCore(services, configure);
    }

    // The single shared registration core: validate the options and bind the store. No TimeProvider is registered
    // because the dedup window is Redis's server-timed EX expiry, not a client clock.
    private static IServiceCollection AddCore(IServiceCollection services, Action<RedisIdempotencyOptions>? configure)
    {
        services.AddOptions<RedisIdempotencyOptions>()
            .Configure(o => configure?.Invoke(o))
            .Validate(o => !string.IsNullOrWhiteSpace(o.KeyPrefix), "KeyPrefix must not be null or whitespace.")
            .Validate(o => o.Retention > TimeSpan.Zero, "Retention must be greater than zero.")
            .ValidateOnStart();

        services.TryAddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
        return services;
    }
}
