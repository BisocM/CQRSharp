using CQRSharp.Persistence;
using CQRSharp.Redis;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     Registration helpers for the CQRSharp Redis-backed outbox store and the inbox store that pairs with it.
/// </summary>
/// <remarks>
///     The stores run on exactly the connection they are registered with. CQRSharp never registers or resolves an
///     <see cref="IConnectionMultiplexer" /> in the container, so an application's own Redis registration neither
///     replaces it nor is replaced by it, and the outbox and the idempotency store can use different servers.
/// </remarks>
public static class RedisOutboxStoreServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the Redis-backed durable <see cref="IOutboxStore" /> and its <see cref="IInboxStore" /> on a
    ///     connection opened from <paramref name="connectionString" />, <b>replacing</b> the outbox and inbox stores already
    ///     registered (the last explicit store registration wins, whatever the order relative to
    ///     <c>AddCqrsGenerated</c>). The connection is opened when the store is first resolved, not at registration; it is
    ///     shared with every CQRSharp Redis store given the same connection string, and closed when the service provider
    ///     is disposed. The outbox processor is always registered by <c>AddCqrsGenerated</c>; the store is used once the
    ///     outbox is on (<c>UseOutbox(...)</c>, or <c>OutboxOptions.Mode</c>). Prefer
    ///     <c>UseOutbox(o =&gt; o.UseRedis(...))</c>, which does both in one step.
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
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return AddCore(services, RedisConnectionSource.Pooled(services, connectionString), configure);
    }

    /// <summary>
    ///     Registers the Redis-backed durable <see cref="IOutboxStore" /> and its <see cref="IInboxStore" /> on the given
    ///     <paramref name="multiplexer" />, <b>replacing</b> the outbox and inbox stores already registered (the last
    ///     explicit store registration wins). The connection stays the application's: CQRSharp never disposes it. The
    ///     outbox processor is always registered by <c>AddCqrsGenerated</c>; the store is used once the outbox is on
    ///     (<c>UseOutbox(...)</c>, or <c>OutboxOptions.Mode</c>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="multiplexer">The Redis connection the stores use.</param>
    /// <param name="configure">Optional callback to adjust <see cref="RedisOutboxOptions" />.</param>
    /// <returns>The same collection for chaining.</returns>
    public static IServiceCollection AddRedisOutboxStore(
        this IServiceCollection services,
        IConnectionMultiplexer multiplexer,
        Action<RedisOutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(multiplexer);
        return AddCore(services, RedisConnectionSource.Given(multiplexer), configure);
    }

    /// <summary>
    ///     Registers the Redis-backed durable <see cref="IOutboxStore" /> and its <see cref="IInboxStore" /> on the
    ///     connection <paramref name="connectionFactory" /> returns, <b>replacing</b> the outbox and inbox stores already
    ///     registered (the last explicit store registration wins). Use it to share a connection the application registers
    ///     itself, such as <c>sp =&gt; sp.GetRequiredService&lt;IConnectionMultiplexer&gt;()</c> or a keyed one. The
    ///     factory runs once per service provider, when the outbox or inbox store is first resolved, and must return a
    ///     connection the application or its container owns: CQRSharp never disposes it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionFactory">Returns the Redis connection the stores use.</param>
    /// <param name="configure">Optional callback to adjust <see cref="RedisOutboxOptions" />.</param>
    /// <returns>The same collection for chaining.</returns>
    public static IServiceCollection AddRedisOutboxStore(
        this IServiceCollection services,
        Func<IServiceProvider, IConnectionMultiplexer> connectionFactory,
        Action<RedisOutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        return AddCore(services, RedisConnectionSource.FromFactory(connectionFactory, nameof(AddRedisOutboxStore)), configure);
    }

    // The one registration path: validated options, a clock, and the store pair on this registration's connection, in
    // place of whatever pair (and connection) was registered before.
    private static IServiceCollection AddCore(
        IServiceCollection services,
        Func<IServiceProvider, IConnectionMultiplexer> connection,
        Action<RedisOutboxOptions>? configure)
    {
        var options = services.AddOptions<RedisOutboxOptions>().ValidateOnStart();
        if (configure is not null)
            options.Configure(configure);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RedisOutboxOptions>, RedisOutboxOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);

        services.RemoveAll<RedisStoreConnection<RedisOutboxOptions>>();
        services.AddSingleton(provider => new RedisStoreConnection<RedisOutboxOptions>(connection(provider)));

        services.RemoveAll<IOutboxStore>();
        services.RemoveAll<IInboxStore>();
        services.AddSingleton<IOutboxStore>(provider => new RedisOutboxStore(
            provider.GetRequiredService<RedisStoreConnection<RedisOutboxOptions>>().Multiplexer,
            provider.GetRequiredService<IOptions<RedisOutboxOptions>>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IInboxStore>(provider => new RedisInboxStore(
            provider.GetRequiredService<RedisStoreConnection<RedisOutboxOptions>>().Multiplexer,
            provider.GetRequiredService<IOptions<RedisOutboxOptions>>()));
        return services;
    }
}
