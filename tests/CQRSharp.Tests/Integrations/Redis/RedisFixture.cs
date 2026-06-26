using StackExchange.Redis;
using Xunit;

namespace CQRSharp.Tests.Integrations.Redis;

/// <summary>
///     Shared Redis connection for the Redis outbox contract tests. Connects once (to the endpoint named by the
///     <c>CQRSHARP_TEST_REDIS</c> environment variable, defaulting to <c>localhost:6379</c>) and PINGs to confirm the
///     server is reachable. When Redis is absent the fixture never throws: <see cref="Available" /> stays false and the
///     tests skip cleanly, so the suite is a no-op on machines without a Redis instance.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private ConnectionMultiplexer? _mux;

    /// <summary>True when a Redis server was reachable; tests skip when false.</summary>
    public bool Available { get; private set; }

    /// <summary>The live connection multiplexer; only valid when <see cref="Available" /> is true.</summary>
    public IConnectionMultiplexer Multiplexer => _mux!;

    public async Task InitializeAsync()
    {
        var endpoint = Environment.GetEnvironmentVariable("CQRSHARP_TEST_REDIS") ?? "localhost:6379";
        try
        {
            _mux = await ConnectionMultiplexer.ConnectAsync(
                $"{endpoint},abortConnect=false,connectTimeout=5000,connectRetry=2");
            await _mux.GetDatabase().PingAsync();
            Available = true;
        }
        catch
        {
            // Redis is not available in this environment; leave Available=false so dependent tests skip.
            Available = false;
            if (_mux is not null)
                await _mux.DisposeAsync();
            _mux = null;
        }
    }

    public async Task DisposeAsync()
    {
        if (_mux is not null)
            await _mux.DisposeAsync();
    }
}
