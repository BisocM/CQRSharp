using System.Text;
using CQRSharp.Tests.Integrations.EntityFrameworkCore.Providers;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;
using Xunit;

namespace CQRSharp.Tests.Integrations.RabbitMQ;

/// <summary>
///     One RabbitMQ broker for the RabbitMQ-backed tests. The broker comes from the <c>CQRSHARP_TEST_RABBITMQ</c> environment
///     variable (an AMQP URI) when it is set (CI's service container), and then a broker that cannot be reached fails the
///     tests rather than skipping them: a configured broker must never turn the RabbitMQ suite into silent skips. Otherwise
///     the fixture starts a Testcontainers RabbitMQ container, and only when that is impossible (no Docker) do the tests
///     skip, with the reason.
/// </summary>
/// <remarks>
///     The tests reach the broker through a <see cref="TcpProxy" /> each, which they can cut to stand for an outage whatever
///     the broker is, and isolate themselves by name: every exchange and queue a test uses carries a prefix of its own
///     (<see cref="NewPrefix" />), so the three target frameworks' test processes can share one broker, and the fixture deletes
///     what a test declared when it is done (<see cref="DeleteAsync" />).
/// </remarks>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    public const string EnvironmentVariable = "CQRSHARP_TEST_RABBITMQ";

    private RabbitMqContainer? _container;
    private IConnection? _connection;

    /// <summary>True once a broker is connected; the tests skip when false.</summary>
    public bool Available { get; private set; }

    /// <summary>Why the tests skip when <see cref="Available" /> is false.</summary>
    public string SkipReason { get; private set; } = "The RabbitMQ broker was not started.";

    /// <summary>The broker's AMQP URI.</summary>
    public Uri Uri { get; private set; } = new("amqp://localhost/");

    /// <summary>The fixture's own connection, for declaring, publishing raw messages and reading queues.</summary>
    public IConnection Connection => _connection ?? throw new InvalidOperationException(SkipReason);

    public async ValueTask InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(external))
        {
            Uri = new Uri(external);
        }
        else
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                _container = new RabbitMqBuilder("rabbitmq:4.1-alpine").Build();
                await _container.StartAsync(timeout.Token);
                Uri = new Uri(_container.GetConnectionString());
            }
            catch (Exception ex)
            {
                SkipReason = $"No RabbitMQ broker: set {EnvironmentVariable} to one or make Docker available for Testcontainers ({ex.GetType().Name}: {ex.Message.Split('\n')[0]}).";
                return;
            }
        }

        // Outside any catch: a configured broker that cannot be reached fails every test of the collection.
        _connection = await new ConnectionFactory { Uri = Uri }.CreateConnectionAsync("cqrsharp-tests/fixture");
        Available = true;
    }

    /// <summary>A prefix no other test uses, for the names of what a test declares.</summary>
    public static string NewPrefix() => $"t-{Guid.NewGuid():N}";

    /// <summary>A proxy to the broker, which the test can cut and restore.</summary>
    public TcpProxy NewProxy() => TcpProxy.Start(Uri.Host, Uri.Port == -1 ? 5672 : Uri.Port);

    /// <summary>The broker's URI through <paramref name="proxy" />.</summary>
    public string UriThrough(TcpProxy proxy) => new UriBuilder(Uri) { Host = "127.0.0.1", Port = proxy.Port }.Uri.ToString();

    /// <summary>A channel on the fixture's connection.</summary>
    public Task<IChannel> ChannelAsync() => Connection.CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>Declares a durable topic exchange and a classic queue bound to it with <paramref name="routingKey" />, to observe what is published.</summary>
    public async Task<string> BindObserverAsync(string exchange, string routingKey, string queue)
    {
        await using var channel = await ChannelAsync();
        await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true, autoDelete: false);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(queue, exchange, routingKey);
        return queue;
    }

    /// <summary>Publishes a raw message, as a producer outside CQRSharp would.</summary>
    public async Task PublishRawAsync(string exchange, string routingKey, byte[] body, BasicProperties properties)
    {
        await using var channel = await Connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        await channel.BasicPublishAsync(exchange, routingKey, mandatory: false, properties, body);
    }

    /// <summary>Takes the next message of <paramref name="queue" />, waiting up to <paramref name="timeout" />; null when none arrives.</summary>
    public async Task<BasicGetResult?> GetAsync(string queue, TimeSpan timeout)
    {
        await using var channel = await ChannelAsync();
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (await channel.BasicGetAsync(queue, autoAck: true) is { } message) return message;
            if (DateTime.UtcNow >= deadline) return null;
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>How many ready messages <paramref name="queue" /> holds.</summary>
    public async Task<uint> CountAsync(string queue)
    {
        await using var channel = await ChannelAsync();
        return (await channel.QueueDeclarePassiveAsync(queue)).MessageCount;
    }

    /// <summary>Deletes the queues and exchanges a test declared.</summary>
    public async Task DeleteAsync(IEnumerable<string> queues, IEnumerable<string> exchanges)
    {
        if (_connection is not { IsOpen: true }) return;

        // One channel per deletion: deleting what does not exist is fine, but a channel-level error would close a shared one.
        foreach (var queue in queues)
            await TryAsync(channel => channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false));
        foreach (var exchange in exchanges)
            await TryAsync(channel => channel.ExchangeDeleteAsync(exchange));
    }

    private async Task TryAsync(Func<IChannel, Task> action)
    {
        try
        {
            await using var channel = await Connection.CreateChannelAsync();
            await action(channel);
        }
        catch (Exception)
        {
            // Cleaning up is best effort: a leftover entity carries a prefix no other test uses.
        }
    }

    /// <summary>The properties of a raw message: a message id, a type, and a persistent delivery.</summary>
    public static BasicProperties Properties(string? messageId, string? type, IDictionary<string, object?>? headers = null)
        => new()
        {
            MessageId = messageId,
            Type = type,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Headers = headers
        };

    /// <summary>A header value as the client hands it back (a string header arrives as bytes).</summary>
    public static string? Header(IReadOnlyBasicProperties properties, string name)
        => properties.Headers is { } headers && headers.TryGetValue(name, out var value)
            ? value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : value?.ToString()
            : null;

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
        if (_container is not null)
            await _container.DisposeAsync();
    }
}

/// <summary>
///     The RabbitMQ-backed tests: one <see cref="RabbitMqFixture" /> (one broker, one connection) for all of them. They run on
///     their own, after the others: a broker round trip is real time, and the tracing test among them subscribes a listener
///     that would change the dispatch path of tests running beside it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RabbitMqCollection : ICollectionFixture<RabbitMqFixture>
{
    public const string Name = "RabbitMQ";
}

/// <summary>A PostgreSQL database of the RabbitMQ tests' own, apart from the PostgreSQL collection's.</summary>
public sealed class RabbitMqPostgreSqlFixture : PostgreSqlFixture
{
    protected override string Area => "rabbitmq";
}

/// <summary>The RabbitMQ tests that take notifications into an EF Core outbox on PostgreSQL.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RabbitMqPostgreSqlCollection : ICollectionFixture<RabbitMqFixture>, ICollectionFixture<RabbitMqPostgreSqlFixture>
{
    public const string Name = "RabbitMQ and PostgreSQL";
}
