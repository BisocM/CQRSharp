using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace CQRSharp.Tests.Integrations.Redis;

/// <summary>
///     One Redis server and one connection for every Redis-backed test (the <see cref="RedisCollection" />). The server
///     comes from the <c>CQRSHARP_TEST_REDIS</c> environment variable when it is set (CI's service container), and then a
///     server that cannot be reached fails the tests rather than skipping them: a configured Redis must never turn the
///     Redis suite into silent skips. Otherwise the fixture starts a Testcontainers Redis container, and only when that is
///     impossible (no Docker) do the tests skip, with the reason.
/// </summary>
/// <remarks>
///     Tests isolate themselves by key prefix (<see cref="NewKeyPrefix" />), so the three target frameworks' test
///     processes can share one server, and delete their keys when they finish (<see cref="DeleteKeysAsync" />).
/// </remarks>
public sealed class RedisFixture : IAsyncLifetime
{
    public const string EnvironmentVariable = "CQRSHARP_TEST_REDIS";

    private RedisContainer? _container;
    private ConnectionMultiplexer? _connection;

    /// <summary>True once a Redis server is connected; the tests skip when false.</summary>
    public bool Available { get; private set; }

    /// <summary>Why the tests skip when <see cref="Available" /> is false.</summary>
    public string SkipReason { get; private set; } = "The Redis server was not started.";

    /// <summary>The server's StackExchange.Redis connection string.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>The shared connection; only valid when <see cref="Available" /> is true.</summary>
    public IConnectionMultiplexer Multiplexer => _connection ?? throw new InvalidOperationException(SkipReason);

    public async ValueTask InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(external))
        {
            ConnectionString = external;
        }
        else
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                _container = new RedisBuilder("redis:7-alpine").Build();
                await _container.StartAsync(timeout.Token);
                ConnectionString = _container.GetConnectionString();
            }
            catch (Exception ex)
            {
                SkipReason = $"No Redis server: set {EnvironmentVariable} to one or make Docker available for Testcontainers ({ex.GetType().Name}: {ex.Message.Split('\n')[0]}).";
                return;
            }
        }

        // Outside any catch: with abortConnect left at its default, a server that cannot be reached throws here and
        // fails every test of the collection.
        _connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
        await _connection.GetDatabase().PingAsync();
        Available = true;
    }

    /// <summary>
    ///     A key prefix no other test uses, carrying the hash tag every store prefix must have. It contains no glob
    ///     metacharacter, so <see cref="DeleteKeysAsync" /> can match it literally.
    /// </summary>
    public static string NewKeyPrefix(string area) => $"{{cqrsharp:test:{area}:{Guid.NewGuid():N}}}:";

    /// <summary>Deletes every key under <paramref name="prefix" />, on every primary the connection knows.</summary>
    public async Task DeleteKeysAsync(string prefix)
    {
        if (_connection is null) return;

        var database = _connection.GetDatabase();
        foreach (var server in _connection.GetServers())
        {
            if (!server.IsConnected || server.IsReplica) continue;

            var keys = new List<RedisKey>();
            await foreach (var key in server.KeysAsync(pattern: prefix + "*"))
                keys.Add(key);

            // The prefix's hash tag keeps every one of these keys in one slot, so a multi-key DEL is valid on a cluster.
            foreach (var batch in keys.Chunk(500))
                await database.KeyDeleteAsync(batch);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
        if (_container is not null)
            await _container.DisposeAsync();
    }
}

/// <summary>The Redis-backed tests: one <see cref="RedisFixture" /> (one server, one connection) for all of them.</summary>
[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "Redis";
}
