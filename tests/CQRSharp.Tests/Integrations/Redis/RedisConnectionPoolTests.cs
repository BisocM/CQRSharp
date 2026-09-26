using System.Net;
using System.Net.Sockets;
using CQRSharp.Persistence;
using CQRSharp.Redis;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace CQRSharp.Tests.Integrations.Redis;

/// <summary>
///     The connections CQRSharp.Redis opens itself, for stores registered with a connection string: one per distinct
///     string and service provider, shared by the stores given that string, and closed with the provider.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class RedisConnectionPoolTests(RedisFixture fixture) : IAsyncLifetime
{
    private readonly string _outboxPrefix = RedisFixture.NewKeyPrefix("pool-outbox");
    private readonly string _idempotencyPrefix = RedisFixture.NewKeyPrefix("pool-idemp");

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await fixture.DeleteKeysAsync(_outboxPrefix);
        await fixture.DeleteKeysAsync(_idempotencyPrefix);
    }

    [Fact(DisplayName = "Stores given the same connection string share one connection, which works for both")]
    public async Task Stores_given_the_same_connection_string_share_one_connection()
    {
        await using var provider = Register(fixture.ConnectionString, fixture.ConnectionString);

        await UseBothStoresAsync(provider);

        provider.GetRequiredService<RedisStoreConnection<RedisIdempotencyOptions>>().Multiplexer.Should()
            .BeSameAs(provider.GetRequiredService<RedisStoreConnection<RedisOutboxOptions>>().Multiplexer);
    }

    [Fact(DisplayName = "Stores given different connection strings get a connection each")]
    public async Task Stores_given_different_connection_strings_get_their_own_connections()
    {
        await using var provider = Register(fixture.ConnectionString, fixture.ConnectionString + ",name=cqrsharp-idempotency");

        await UseBothStoresAsync(provider);

        provider.GetRequiredService<RedisStoreConnection<RedisIdempotencyOptions>>().Multiplexer.Should()
            .NotBeSameAs(provider.GetRequiredService<RedisStoreConnection<RedisOutboxOptions>>().Multiplexer);
    }

    [Theory(DisplayName = "Disposing the service provider closes the connections the pool opened")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Disposing_the_provider_closes_the_pooled_connections(bool disposeAsynchronously)
    {
        var provider = Register(fixture.ConnectionString, fixture.ConnectionString + ",name=cqrsharp-idempotency");
        await UseBothStoresAsync(provider);
        IConnectionMultiplexer[] opened =
        [
            provider.GetRequiredService<RedisStoreConnection<RedisOutboxOptions>>().Multiplexer,
            provider.GetRequiredService<RedisStoreConnection<RedisIdempotencyOptions>>().Multiplexer
        ];
        opened.Should().OnlyContain(connection => connection.IsConnected);

        if (disposeAsynchronously)
            await provider.DisposeAsync();
        else
            provider.Dispose();

        opened.Should().OnlyContain(connection => !connection.IsConnected, "the provider owned the connections its pool opened");
    }

    [Fact(DisplayName = "A connection that failed to open is tried again by the next resolution, not remembered as failed")]
    public async Task A_failed_connect_is_retried_by_the_next_resolution()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var server = ConfigurationOptions.Parse(fixture.ConnectionString).EndPoints[0];
        var port = FreeLoopbackPort();
        var connectionString = $"127.0.0.1:{port},connectTimeout=2000,connectRetry=1";
        using var pool = new RedisConnectionPool();

        // Nothing listens yet: the connect fails (abortConnect is on by default) and throws to this resolution.
        FluentActions.Invoking(() => pool.Get(connectionString)).Should().Throw<RedisConnectionException>();

        await using var forwarder = LoopbackForwarder.Start(port, server);
        var connection = pool.Get(connectionString);

        connection.IsConnected.Should().BeTrue("the server is reachable now, and the earlier failure was not kept");
        (await connection.GetDatabase().PingAsync()).Should().BePositive();
    }

    [Fact(DisplayName = "A disposed pool opens no more connections")]
    public void A_disposed_pool_opens_no_more_connections()
    {
        var pool = new RedisConnectionPool();
        pool.Dispose();

        FluentActions.Invoking(() => pool.Get("127.0.0.1:1")).Should().Throw<ObjectDisposedException>();
    }

    private ServiceProvider Register(string outboxConnection, string idempotencyConnection)
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var services = new ServiceCollection();
        services.AddRedisOutboxStore(outboxConnection, o => o.KeyPrefix = _outboxPrefix);
        services.AddRedisIdempotencyStore(idempotencyConnection, o => o.KeyPrefix = _idempotencyPrefix);
        return services.BuildServiceProvider();
    }

    private static async Task UseBothStoresAsync(IServiceProvider provider)
    {
        (await provider.GetRequiredService<IOutboxStore>().GetBacklogAsync(Cancel)).PendingCount.Should().Be(0);
        (await provider.GetRequiredService<IIdempotencyStore>().TryClaimAsync("key", null, Cancel)).IsClaimed.Should().BeTrue();
    }

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // Accepts connections on a loopback port and pipes each one to the test server, so a server can appear at an
    // address only after a connect to it has failed.
    private sealed class LoopbackForwarder : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly EndPoint _target;
        private readonly CancellationTokenSource _stop = new();
        private readonly List<IDisposable> _sockets = [];
        private readonly List<Task> _pumps = [];
        private readonly Task _accepting;

        private LoopbackForwarder(int port, EndPoint target)
        {
            _target = target;
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _accepting = AcceptAsync();
        }

        public static LoopbackForwarder Start(int port, EndPoint target) => new(port, target);

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var upstream = new TcpClient();
                    lock (_sockets)
                    {
                        _sockets.Add(client);
                        _sockets.Add(upstream);
                    }

                    await (_target switch
                    {
                        DnsEndPoint dns => upstream.ConnectAsync(dns.Host, dns.Port, _stop.Token),
                        IPEndPoint ip => upstream.ConnectAsync(ip, _stop.Token),
                        _ => throw new NotSupportedException(_target.ToString())
                    });
                    lock (_sockets)
                    {
                        _pumps.Add(client.GetStream().CopyToAsync(upstream.GetStream(), _stop.Token));
                        _pumps.Add(upstream.GetStream().CopyToAsync(client.GetStream(), _stop.Token));
                    }
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                // Stopping: the pending accept or connect ends with the cancellation.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _accepting;

            Task[] pumps;
            lock (_sockets)
            {
                foreach (var socket in _sockets)
                    socket.Dispose();
                pumps = [.. _pumps];
            }

            try
            {
                await Task.WhenAll(pumps);
            }
            catch (Exception)
            {
                // The pumps end with the sockets they copy between, most of them with an error; that is the point.
            }

            _stop.Dispose();
        }
    }
}
