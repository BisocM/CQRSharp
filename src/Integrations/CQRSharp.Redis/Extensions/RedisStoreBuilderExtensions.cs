using CQRSharp.Core.Extensions;
using CQRSharp.Redis.Idempotency;
using CQRSharp.Redis.Outbox;
using StackExchange.Redis;

// Namespace-extends the DI builder so the fluent store verbs read naturally next to the rest of the app's wiring.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     Fluent store verbs for the Redis integration, used inside the builder's <c>UseOutbox(...)</c> and
///     <c>UseIdempotency(...)</c> so the Redis store can be selected in the same call. Each verb has an overload that
///     takes a connection string (the shared multiplexer is created lazily) and one that takes an existing
///     <see cref="IConnectionMultiplexer" /> so a single Redis connection can be shared across stores.
/// </summary>
public static class RedisStoreBuilderExtensions
{
    /// <summary>Uses the Redis durable outbox store, creating the shared multiplexer from <paramref name="connectionString" />.</summary>
    public static OutboxStoreBuilder UseRedis(
        this OutboxStoreBuilder builder,
        string connectionString,
        Action<RedisOutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseStore(services => services.AddRedisOutboxStore(connectionString, configure));
    }

    /// <summary>Uses the Redis durable outbox store against an already-constructed <paramref name="multiplexer" />.</summary>
    public static OutboxStoreBuilder UseRedis(
        this OutboxStoreBuilder builder,
        IConnectionMultiplexer multiplexer,
        Action<RedisOutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseStore(services => services.AddRedisOutboxStore(multiplexer, configure));
    }

    /// <summary>Uses the Redis durable idempotency store, creating the shared multiplexer from <paramref name="connectionString" />.</summary>
    public static IdempotencyStoreBuilder UseRedis(
        this IdempotencyStoreBuilder builder,
        string connectionString,
        Action<RedisIdempotencyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseStore(services => services.AddRedisIdempotencyStore(connectionString, configure));
    }

    /// <summary>Uses the Redis durable idempotency store against an already-constructed <paramref name="multiplexer" />.</summary>
    public static IdempotencyStoreBuilder UseRedis(
        this IdempotencyStoreBuilder builder,
        IConnectionMultiplexer multiplexer,
        Action<RedisIdempotencyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseStore(services => services.AddRedisIdempotencyStore(multiplexer, configure));
    }
}
