using CQRSharp.Pipelines;
using CQRSharp.Redis;
using StackExchange.Redis;

// Namespace-extends the DI builder so the fluent store verbs read naturally next to the rest of the app's wiring.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     Fluent store verbs for the Redis integration, used inside the builder's <c>UseOutbox(...)</c> and
///     <c>UseIdempotency(...)</c> so the Redis store can be selected in the same call. Each store runs on exactly the
///     connection it is given: a connection string (opened on first use, shared by the stores given the same string, and
///     closed with the service provider), an existing <see cref="IConnectionMultiplexer" />, or a factory that returns
///     one the application registers itself. The outbox and the idempotency store may use different servers.
/// </summary>
public static class RedisStoreBuilderExtensions
{
    /// <summary>Uses the Redis durable outbox store on a connection opened from <paramref name="connectionString" />.</summary>
    /// <param name="builder">The outbox builder.</param>
    /// <param name="connectionString">The StackExchange.Redis connection string.</param>
    /// <param name="configure">Optional callback to adjust <see cref="RedisOutboxOptions" />.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static OutboxStoreBuilder UseRedis(
        this OutboxStoreBuilder builder,
        string connectionString,
        Action<RedisOutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return builder.UseStore(services => services.AddRedisOutboxStore(connectionString, configure));
    }

    /// <summary>Uses the Redis durable outbox store on <paramref name="multiplexer" />, which CQRSharp never disposes.</summary>
    /// <param name="builder">The outbox builder.</param>
    /// <param name="multiplexer">The Redis connection the stores use.</param>
    /// <param name="configure">Optional callback to adjust <see cref="RedisOutboxOptions" />.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static OutboxStoreBuilder UseRedis(
        this OutboxStoreBuilder builder,
        IConnectionMultiplexer multiplexer,
        Action<RedisOutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(multiplexer);
        return builder.UseStore(services => services.AddRedisOutboxStore(multiplexer, configure));
    }

    /// <summary>
    ///     Uses the Redis durable outbox store on the connection <paramref name="connectionFactory" /> returns, such as
    ///     <c>sp =&gt; sp.GetRequiredService&lt;IConnectionMultiplexer&gt;()</c>. The factory runs once per service
    ///     provider, and CQRSharp never disposes what it returns.
    /// </summary>
    /// <param name="builder">The outbox builder.</param>
    /// <param name="connectionFactory">Returns the Redis connection the stores use.</param>
    /// <param name="configure">Optional callback to adjust <see cref="RedisOutboxOptions" />.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static OutboxStoreBuilder UseRedis(
        this OutboxStoreBuilder builder,
        Func<IServiceProvider, IConnectionMultiplexer> connectionFactory,
        Action<RedisOutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        return builder.UseStore(services => services.AddRedisOutboxStore(connectionFactory, configure));
    }

    /// <summary>Uses the Redis durable idempotency store on a connection opened from <paramref name="connectionString" />.</summary>
    /// <param name="builder">The idempotency builder.</param>
    /// <param name="connectionString">The StackExchange.Redis connection string.</param>
    /// <param name="configure">Optional callback to adjust <see cref="RedisIdempotencyOptions" />.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IdempotencyStoreBuilder UseRedis(
        this IdempotencyStoreBuilder builder,
        string connectionString,
        Action<RedisIdempotencyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return builder.UseStore(services => services.AddRedisIdempotencyStore(connectionString, configure));
    }

    /// <summary>Uses the Redis durable idempotency store on <paramref name="multiplexer" />, which CQRSharp never disposes.</summary>
    /// <param name="builder">The idempotency builder.</param>
    /// <param name="multiplexer">The Redis connection the store uses.</param>
    /// <param name="configure">Optional callback to adjust <see cref="RedisIdempotencyOptions" />.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IdempotencyStoreBuilder UseRedis(
        this IdempotencyStoreBuilder builder,
        IConnectionMultiplexer multiplexer,
        Action<RedisIdempotencyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(multiplexer);
        return builder.UseStore(services => services.AddRedisIdempotencyStore(multiplexer, configure));
    }

    /// <summary>
    ///     Uses the Redis durable idempotency store on the connection <paramref name="connectionFactory" /> returns, such
    ///     as <c>sp =&gt; sp.GetRequiredService&lt;IConnectionMultiplexer&gt;()</c>. The factory runs once per service
    ///     provider, and CQRSharp never disposes what it returns.
    /// </summary>
    /// <param name="builder">The idempotency builder.</param>
    /// <param name="connectionFactory">Returns the Redis connection the store uses.</param>
    /// <param name="configure">Optional callback to adjust <see cref="RedisIdempotencyOptions" />.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IdempotencyStoreBuilder UseRedis(
        this IdempotencyStoreBuilder builder,
        Func<IServiceProvider, IConnectionMultiplexer> connectionFactory,
        Action<RedisIdempotencyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        return builder.UseStore(services => services.AddRedisIdempotencyStore(connectionFactory, configure));
    }
}
