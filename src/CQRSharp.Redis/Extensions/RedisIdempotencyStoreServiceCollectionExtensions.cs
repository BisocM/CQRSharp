using CQRSharp.Persistence;
using CQRSharp.Redis;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     Registration helpers for the CQRSharp Redis-backed idempotency store.
/// </summary>
/// <remarks>
///     The store runs on exactly the connection it is registered with. CQRSharp never registers or resolves an
///     <see cref="IConnectionMultiplexer" /> in the container, so an application's own Redis registration neither
///     replaces it nor is replaced by it, and the idempotency store and the outbox can use different servers.
/// </remarks>
public static class RedisIdempotencyStoreServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the Redis-backed durable <see cref="IIdempotencyStore" /> on a connection opened from
    ///     <paramref name="connectionString" />, <b>replacing</b> the idempotency store already registered (the last
    ///     explicit store registration wins, whatever the order relative to <c>AddCqrsGenerated</c>). The connection is
    ///     opened when the store is first resolved, not at registration; it is shared with every CQRSharp Redis store given
    ///     the same connection string, and closed when the service provider is disposed. Prefer
    ///     <c>UseIdempotency(i =&gt; i.UseRedis(...))</c>, which also turns on the idempotency behavior.
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
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return AddCore(services, RedisConnectionSource.Pooled(services, connectionString), configure);
    }

    /// <summary>
    ///     Registers the Redis-backed durable <see cref="IIdempotencyStore" /> on the given
    ///     <paramref name="multiplexer" />, <b>replacing</b> the idempotency store already registered (the last explicit
    ///     store registration wins). The connection stays the application's: CQRSharp never disposes it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="multiplexer">The Redis connection the store uses.</param>
    /// <param name="configure">Optional callback to adjust <see cref="RedisIdempotencyOptions" />.</param>
    /// <returns>The same collection for chaining.</returns>
    public static IServiceCollection AddRedisIdempotencyStore(
        this IServiceCollection services,
        IConnectionMultiplexer multiplexer,
        Action<RedisIdempotencyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(multiplexer);
        return AddCore(services, RedisConnectionSource.Given(multiplexer), configure);
    }

    /// <summary>
    ///     Registers the Redis-backed durable <see cref="IIdempotencyStore" /> on the connection
    ///     <paramref name="connectionFactory" /> returns, <b>replacing</b> the idempotency store already registered (the
    ///     last explicit store registration wins). Use it to share a connection the application registers itself, such as
    ///     <c>sp =&gt; sp.GetRequiredService&lt;IConnectionMultiplexer&gt;()</c> or a keyed one. The factory runs once per
    ///     service provider, when the store is first resolved, and must return a connection the application or its
    ///     container owns: CQRSharp never disposes it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionFactory">Returns the Redis connection the store uses.</param>
    /// <param name="configure">Optional callback to adjust <see cref="RedisIdempotencyOptions" />.</param>
    /// <returns>The same collection for chaining.</returns>
    public static IServiceCollection AddRedisIdempotencyStore(
        this IServiceCollection services,
        Func<IServiceProvider, IConnectionMultiplexer> connectionFactory,
        Action<RedisIdempotencyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        return AddCore(services, RedisConnectionSource.FromFactory(connectionFactory, nameof(AddRedisIdempotencyStore)), configure);
    }

    // The one registration path: validated options and the store on this registration's connection, in place of whatever
    // idempotency store was registered before. No clock is registered: the deduplication window is the keys' PX expiry,
    // which Redis times itself.
    private static IServiceCollection AddCore(
        IServiceCollection services,
        Func<IServiceProvider, IConnectionMultiplexer> connection,
        Action<RedisIdempotencyOptions>? configure)
    {
        var options = services.AddOptions<RedisIdempotencyOptions>().ValidateOnStart();
        if (configure is not null)
            options.Configure(configure);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RedisIdempotencyOptions>, RedisIdempotencyOptionsValidator>());

        services.RemoveAll<RedisStoreConnection<RedisIdempotencyOptions>>();
        services.AddSingleton(provider => new RedisStoreConnection<RedisIdempotencyOptions>(connection(provider)));

        services.RemoveAll<IIdempotencyStore>();
        services.AddSingleton<IIdempotencyStore>(provider => new RedisIdempotencyStore(
            provider.GetRequiredService<RedisStoreConnection<RedisIdempotencyOptions>>().Multiplexer,
            provider.GetRequiredService<IOptions<RedisIdempotencyOptions>>()));
        return services;
    }
}
