using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Redis.Extensions;

/// <summary>
///     The Redis stores share one <c>IConnectionMultiplexer</c>, registered by whichever <c>UseRedis(connectionString)</c>
///     runs first. A second, different connection string used to be dropped silently — both stores then talked to the
///     first server. This records the connection string at registration time and rejects a conflicting one.
/// </summary>
internal static class RedisConnectionGuard
{
    public static void EnsureSingleConnectionString(IServiceCollection services, string connectionString)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ImplementationInstance is not RegisteredConnectionString registered) continue;
            if (string.Equals(registered.Value, connectionString, StringComparison.Ordinal)) return;

            throw new InvalidOperationException(
                "CQRSharp.Redis was given two different Redis connection strings. The outbox and idempotency stores share a " +
                "single IConnectionMultiplexer, so the second one would be ignored. Use the same connection string for both, " +
                "or register the multiplexer(s) yourself and pass them to UseRedis(IConnectionMultiplexer).");
        }

        services.AddSingleton(new RegisteredConnectionString(connectionString));
    }

    private sealed record RegisteredConnectionString(string Value);
}
