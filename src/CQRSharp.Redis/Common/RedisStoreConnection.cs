using StackExchange.Redis;

namespace CQRSharp.Redis;

/// <summary>
///     The connection one Redis store feature runs on, the feature named by its options type
///     (<see cref="RedisOutboxOptions" /> for the outbox and its inbox, <see cref="RedisIdempotencyOptions" /> for the
///     idempotency store). Resolved once per service provider from what the feature's registration was given, so an
///     application's connection factory runs once per feature, and the outbox and its inbox share one connection.
/// </summary>
/// <typeparam name="TOptions">The options type of the feature the connection belongs to.</typeparam>
internal sealed class RedisStoreConnection<TOptions>(IConnectionMultiplexer multiplexer) where TOptions : class
{
    public IConnectionMultiplexer Multiplexer { get; } = multiplexer;
}
