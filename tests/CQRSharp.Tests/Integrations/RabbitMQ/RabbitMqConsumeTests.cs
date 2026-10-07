using System.Globalization;
using System.Text;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using CQRSharp.RabbitMQ;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;
using Xunit;

namespace CQRSharp.Tests.Integrations.RabbitMQ;

/// <summary>
///     Consuming through the RabbitMQ transport, on a real broker: a received notification is taken into the outbox and
///     acknowledged only once stored, then delivered to the local handler; a redelivery is recognised by its message id; what
///     cannot be read, carries no type or stays unknown goes to the dead-letter queue; what cannot be taken in yet is held,
///     then returned; per-key order holds through the lanes and across instances with a single active consumer; a stop lets
///     the held messages go back; a subscription or channel that is lost is taken up again; a topology the broker refuses is
///     reported.
/// </summary>
[Collection(RabbitMqCollection.Name)]
public sealed class RabbitMqConsumeTests(RabbitMqFixture fixture) : IAsyncLifetime
{
    private const string OrderName = "tests.rabbit.order-placed";

    private readonly string _prefix = RabbitMqFixture.NewPrefix();
    private readonly List<IAsyncDisposable> _owned = [];

    private string Exchange => $"{_prefix}.x";
    private string DeadLetterExchange => $"{_prefix}.dlx";
    private string Queue => $"{_prefix}.q";
    private string DeadLetterQueue => $"{_prefix}.q.dead-letter";

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
            await _owned[i].DisposeAsync();
        await fixture.DeleteAsync([Queue, DeadLetterQueue], [Exchange, DeadLetterExchange]);
    }

    private TcpProxy Proxy()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var proxy = fixture.NewProxy();
        _owned.Add(proxy);
        return proxy;
    }

    private void Configure(RabbitMqTransportBuilder r)
        => r.Configure(o =>
        {
            o.DefaultExchange = Exchange;
            o.DeadLetterExchange = DeadLetterExchange;
            o.ReconnectMaxDelay = TimeSpan.FromSeconds(1);
        });

    private async Task<RabbitMqTestHost> ConsumerAsync(
        Action<RabbitMqConsumerBuilder>? queue = null,
        TcpProxy? proxy = null,
        Action<ICqrsBuilder>? cqrs = null,
        Action<OutboxStoreBuilder>? outbox = null,
        bool waitUntilConsuming = true)
    {
        var host = await RabbitMqTestHost.StartAsync(fixture.UriThrough(proxy ?? Proxy()), r =>
        {
            Configure(r);
            r.Consume(Queue, q =>
            {
                q.Bind<RabbitOrderPlaced>();
                queue?.Invoke(q);
            });
        }, outbox, cqrs, services => services.AddHealthChecks().AddCqrsRabbitMq());
        _owned.Add(host);
        if (waitUntilConsuming) await host.Logs.WaitForAsync(8020);
        return host;
    }

    private async Task<RabbitMqTestHost> ProducerAsync()
    {
        var host = await RabbitMqTestHost.StartAsync(fixture.UriThrough(Proxy()), r =>
        {
            Configure(r);
            r.Publish<RabbitOrderPlaced>();
        });
        _owned.Add(host);
        return host;
    }

    private static byte[] Payload(RabbitMqTestHost host, INotification notification)
        => host.Services.GetRequiredService<INotificationSerializer>().Serialize(notification);

    private Task PublishRawAsync(byte[] body, string? messageId, string? type, IDictionary<string, object?>? headers = null)
        => fixture.PublishRawAsync(Exchange, OrderName, body, RabbitMqFixture.Properties(messageId, type, headers));

    private static async Task<HealthReportEntry> HealthAsync(RabbitMqTestHost host)
        => (await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken)).Entries["cqrsharp.rabbitmq"];

    [Fact(DisplayName = "RabbitMQ consume: a notification published by one application reaches the other's handler once, and is acknowledged")]
    public async Task A_notification_crosses_the_broker()
    {
        var consumer = await ConsumerAsync();
        var producer = await ProducerAsync();

        await producer.PublishAsync(new RabbitOrderPlaced(1, "order-1"));

        await consumer.Received.WaitForAsync(1);
        await producer.Received.WaitForAsync(1);
        consumer.Received.Orders.Should().Equal(new RabbitOrderPlaced(1, "order-1"));
        producer.Received.Orders.Should().Equal([new RabbitOrderPlaced(1, "order-1")], "a notification sent to the broker still reaches its local handlers");
        (await HealthAsync(consumer)).Status.Should().Be(HealthStatus.Healthy);

        await consumer.StopAsync();
        (await fixture.CountAsync(Queue)).Should().Be(0u, "the message was acknowledged, so the stop returned nothing to the queue");
        consumer.Received.Orders.Should().ContainSingle();
    }

    [Fact(DisplayName = "RabbitMQ consume: a redelivery with the same message id reaches the handler once")]
    public async Task A_duplicate_message_id_is_taken_in_once()
    {
        var consumer = await ConsumerAsync(q => q.Lanes(1));
        var messageId = Guid.NewGuid().ToString("N");

        await PublishRawAsync(Payload(consumer, new RabbitOrderPlaced(1, "k")), messageId, OrderName);
        await PublishRawAsync(Payload(consumer, new RabbitOrderPlaced(1, "k")), messageId, OrderName);
        await PublishRawAsync(Payload(consumer, new RabbitOrderPlaced(2, "k")), Guid.NewGuid().ToString("N"), OrderName);

        await consumer.Received.WaitForAsync(2);
        consumer.Received.Orders.Select(o => o.Seq).Should().Equal(1, 2);
        consumer.Logs.WithId(5101).Should().ContainSingle("the second delivery of the first message was recognised");
    }

    [Fact(DisplayName = "RabbitMQ consume: a payload that cannot be read, or a message without a type, goes to the dead-letter queue")]
    public async Task Unreadable_and_untyped_messages_are_dead_lettered()
    {
        var consumer = await ConsumerAsync();

        await PublishRawAsync("{ not json"u8.ToArray(), "m-1", OrderName);
        await PublishRawAsync(Payload(consumer, new RabbitOrderPlaced(1, "k")), "m-2", type: null);

        var deadLettered = await fixture.ReceiveAsync(DeadLetterQueue, 2);
        deadLettered.Select(m => m.BasicProperties.MessageId).Should().BeEquivalentTo("m-1", "m-2");
        consumer.Logs.WithId(8030).Should().ContainSingle();
        consumer.Logs.WithId(8034).Should().ContainSingle();
        consumer.Received.Orders.Should().BeEmpty();
    }

    [Theory(DisplayName = "RabbitMQ consume: an unknown notification is held for the grace period, then dead-lettered, aged from its creation time or, without one, from its arrival")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_unknown_notification_is_held_then_dead_lettered(bool withCreationTime)
    {
        var consumer = await ConsumerAsync(cqrs: b => b.UseOutbox(o => o.ConfigureProcessor(p => p.UnknownRecipientGracePeriod = TimeSpan.FromSeconds(2))));
        var createdAt = withCreationTime
            ? new Dictionary<string, object?> { ["cqrsharp-created-at"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) }
            : null;

        await PublishRawAsync("{}"u8.ToArray(), "m-1", "tests.rabbit.added-by-a-newer-version", createdAt);

        await consumer.Logs.WaitForAsync(8031);
        (await fixture.ReceiveAsync(DeadLetterQueue)).BasicProperties.MessageId.Should().Be("m-1", "past its grace period nobody is going to know it");
        consumer.Logs.WithId(8032).Should().ContainSingle();
    }

    [Fact(DisplayName = "RabbitMQ consume: a message that cannot be stored is held and retried up to MaxHold, returned to the queue, then taken in once the store is back")]
    public async Task A_message_that_cannot_be_stored_is_held_then_returned()
    {
        var log = new TransactionLog();
        var store = new RecordingOutboxStore(false, log) { StoreFailure = new InvalidOperationException("the outbox store is down") };
        var consumer = await ConsumerAsync(
            q => q.MaxHold(TimeSpan.FromSeconds(1)),
            outbox: o => o.UseStore(s =>
            {
                // The in-memory inbox, and an outbox store that fails every write until the test clears it.
                s.AddInMemoryOutboxStore();
                s.RemoveAll<IOutboxStore>();
                s.AddSingleton<IOutboxStore>(store);
            }));

        await PublishRawAsync(Payload(consumer, new RabbitOrderPlaced(1, "k")), "m-1", OrderName);

        await consumer.Logs.WaitForAsync(8033);
        await consumer.Logs.WaitForAsync(8035);
        store.StoreFailure = null;
        await consumer.Received.WaitForAsync(1);
        consumer.Received.Orders.Should().ContainSingle();
    }

    [Fact(DisplayName = "RabbitMQ consume: across lanes, the notifications of each key reach the handler in the order they were published")]
    public async Task Per_key_order_holds_across_lanes()
    {
        var consumer = await ConsumerAsync(q => q.Lanes(4).Prefetch(32));
        var producer = await ProducerAsync();
        var keys = new[] { "a", "b", "c" };

        await producer.PublishAsync(Enumerable.Range(1, 30).Select(i => (INotification)new RabbitOrderPlaced(i, keys[i % 3])).ToArray());

        await consumer.Received.WaitForAsync(30);
        foreach (var key in keys)
            consumer.Received.Orders.Where(o => o.Key == key).Select(o => o.Seq).Should().BeInAscendingOrder($"key {key} keeps its order");
    }

    [Fact(DisplayName = "RabbitMQ consume: with a single active consumer one instance takes the queue, in order, and the other stands by")]
    public async Task A_single_active_consumer_takes_the_queue()
    {
        var first = await ConsumerAsync(q => q.SingleActiveConsumer());
        var second = await ConsumerAsync(q => q.SingleActiveConsumer());
        var producer = await ProducerAsync();

        await producer.PublishAsync(Enumerable.Range(1, 10).Select(i => (INotification)new RabbitOrderPlaced(i, "k")).ToArray());

        await Task.WhenAny(first.Received.WaitForAsync(10), second.Received.WaitForAsync(10));
        var (active, standby) = first.Received.Orders.Count == 10 ? (first, second) : (second, first);
        active.Received.Orders.Select(o => o.Seq).Should().Equal(Enumerable.Range(1, 10));
        standby.Received.Orders.Should().BeEmpty();
    }

    [Fact(DisplayName = "RabbitMQ consume: a stop lets a held message go back to the queue, unacknowledged")]
    public async Task A_stop_releases_held_messages()
    {
        var consumer = await ConsumerAsync();

        await PublishRawAsync("{}"u8.ToArray(), "m-1", "tests.rabbit.added-by-a-newer-version");
        await consumer.Logs.WaitForAsync(8031);
        await consumer.StopAsync();

        consumer.Logs.WithId(8023).Should().ContainSingle().Which.Message.Should().Contain("released 1");
        (await fixture.ReceiveAsync(Queue)).BasicProperties.MessageId
            .Should().Be("m-1", "the broker returned the unacknowledged message to the queue when the channel closed");
    }

    [Fact(DisplayName = "RabbitMQ consume: a subscription the broker cancels (the queue was deleted) is declared and consumed again")]
    public async Task A_cancelled_subscription_is_taken_up_again()
    {
        var consumer = await ConsumerAsync();
        var producer = await ProducerAsync();

        await using (var channel = await fixture.ChannelAsync())
            await channel.QueueDeleteAsync(Queue, ifUnused: false, ifEmpty: false, cancellationToken: TestContext.Current.CancellationToken);
        await consumer.Logs.WaitForAsync(8022);
        await consumer.Logs.WaitForAsync(8020, count: 2);

        await producer.PublishAsync(new RabbitOrderPlaced(5, "k"));
        await consumer.Received.WaitForAsync(1);
    }

    [Fact(DisplayName = "RabbitMQ consume: a lost connection is reopened and the queue consumed again")]
    public async Task A_lost_connection_is_consumed_again()
    {
        var proxy = Proxy();
        var consumer = await ConsumerAsync(proxy: proxy);
        var producer = await ProducerAsync();

        proxy.Cut();
        await consumer.Logs.WaitForAsync(8001);
        (await HealthAsync(consumer)).Status.Should().Be(HealthStatus.Unhealthy);
        proxy.Restore();
        await consumer.Logs.WaitForAsync(8020, count: 2);

        await producer.PublishAsync(new RabbitOrderPlaced(9, "k"));
        await consumer.Received.WaitForAsync(1);
        (await HealthAsync(consumer)).Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact(DisplayName = "RabbitMQ consume: a queue that exists with other settings is refused, logged and reported unhealthy")]
    public async Task A_refused_topology_is_reported()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        await using (var channel = await fixture.ChannelAsync())
            await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?> { ["x-queue-type"] = "classic" }, cancellationToken: TestContext.Current.CancellationToken);

        var consumer = await ConsumerAsync(q => q.DeadLetterQueue(false), waitUntilConsuming: false);

        await consumer.Logs.WaitForAsync(8011);
        var health = await HealthAsync(consumer);
        health.Status.Should().Be(HealthStatus.Unhealthy);
        health.Description.Should().Contain("PRECONDITION_FAILED");
        health.Data[$"rabbitmq.queue.{Queue}"].Should().Be("Refused");
    }
}
