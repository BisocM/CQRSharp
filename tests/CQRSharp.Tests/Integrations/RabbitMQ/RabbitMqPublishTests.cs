using System.Text;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Persistence;
using CQRSharp.RabbitMQ;
using CQRSharp.Tests.Core;
using CQRSharp.Transports;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RabbitMQ.Client;
using Xunit;

namespace CQRSharp.Tests.Integrations.RabbitMQ;

/// <summary>
///     Publishing through the RabbitMQ transport, on a real broker: what a published notification carries, what counts as
///     sent (a confirmed, mandatory, persistent publish), how an unroutable publish, a missing exchange and an outage end, and
///     that the outbox forwards a notification, in order per key, when it is due.
/// </summary>
[Collection(RabbitMqCollection.Name)]
public sealed class RabbitMqPublishTests(RabbitMqFixture fixture) : IAsyncLifetime
{
    private const string OrderName = "tests.rabbit.order-placed";

    private readonly string _prefix = RabbitMqFixture.NewPrefix();
    private readonly List<IAsyncDisposable> _owned = [];

    private string Exchange => $"{_prefix}.x";
    private string Observer => $"{_prefix}.observer";

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
            await _owned[i].DisposeAsync();
        await fixture.DeleteAsync([Observer], [Exchange, $"{_prefix}.other"]);
    }

    private TcpProxy Proxy()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var proxy = fixture.NewProxy();
        _owned.Add(proxy);
        return proxy;
    }

    private async Task<RabbitMqTestHost> HostAsync(TcpProxy proxy, Action<RabbitMqTransportBuilder> transport, TimeProvider? time = null)
    {
        var host = await RabbitMqTestHost.StartAsync(fixture.UriThrough(proxy), r =>
        {
            r.Configure(o =>
            {
                o.DefaultExchange = Exchange;
                o.ReconnectMaxDelay = TimeSpan.FromSeconds(1);
                o.PublishTimeout = TimeSpan.FromSeconds(5);
            });
            transport(r);
        }, time: time);
        _owned.Add(host);
        await Runtime(host).Publisher.WaitAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static RabbitMqTransportRuntime Runtime(RabbitMqTestHost host)
        => host.Services.GetRequiredKeyedService<RabbitMqTransportRuntime>(RabbitMqTransportOptions.DefaultTransportName);

    [Fact(DisplayName = "RabbitMQ publish: a notification is published persistent, identified, typed by its name, and with the CQRSharp headers")]
    public async Task A_published_notification_carries_its_properties()
    {
        var proxy = Proxy();
        await fixture.BindObserverAsync(Exchange, OrderName, Observer);
        var host = await HostAsync(proxy, r => r.Publish<RabbitOrderPlaced>());

        await host.PublishAsync(new RabbitOrderPlaced(1, "order-1"));

        var message = await fixture.ReceiveAsync(Observer);
        var properties = message.BasicProperties;
        properties.Type.Should().Be(OrderName);
        Guid.TryParseExact(properties.MessageId, "N", out _).Should().BeTrue("the message id is the outbox message's id");
        properties.DeliveryMode.Should().Be(DeliveryModes.Persistent);
        properties.ContentType.Should().Be("application/json");
        properties.Timestamp.UnixTime.Should().BePositive();
        RabbitMqFixture.Header(properties, "cqrsharp-partition-key").Should().Be("order-1");
        Guid.TryParse(RabbitMqFixture.Header(properties, "cqrsharp-notification-id"), out _).Should().BeTrue();
        DateTime.TryParse(RabbitMqFixture.Header(properties, "cqrsharp-created-at"), out _).Should().BeTrue();
        host.Services.GetRequiredService<INotificationSerializer>().Deserialize(OrderName, message.Body)
            .Should().Be(new RabbitOrderPlaced(1, "order-1"));
        message.RoutingKey.Should().Be(OrderName);
    }

    [Fact(DisplayName = "RabbitMQ publish: a publish no queue is bound to receive is rejected, unless the publication allows it")]
    public async Task An_unroutable_publish_is_rejected_unless_allowed()
    {
        var proxy = Proxy();
        var host = await HostAsync(proxy, r => r.Publish<RabbitOrderPlaced>().Publish<RabbitBroadcast>(p => p.AllowUnroutable()));
        var transport = Runtime(host).Transport;

        var rejected = await transport.SendAsync(RabbitMqRegistrationTests.Outbound(OrderName, [1]), TestContext.Current.CancellationToken);
        var allowed = await transport.SendAsync(RabbitMqRegistrationTests.Outbound("tests.rabbit.broadcast", [1]), TestContext.Current.CancellationToken);

        rejected.Status.Should().Be(TransportSendStatus.Rejected);
        rejected.Permanent.Should().BeFalse("a queue bound later can receive it: it is retried, then dead-lettered");
        rejected.Reason.Should().Contain("No queue is bound").And.Contain(Exchange);
        allowed.Should().Be(TransportSendResult.Sent);
        host.Logs.WithId(8040).Should().HaveCount(2);
    }

    [Fact(DisplayName = "RabbitMQ publish: with AssumeExistingTopology a missing exchange is a rejection, and the next publish works on a fresh channel")]
    public async Task A_missing_exchange_is_rejected_and_the_channel_recovers()
    {
        var proxy = Proxy();
        await fixture.BindObserverAsync($"{_prefix}.other", "tests.rabbit.broadcast", Observer);
        var host = await HostAsync(proxy, r => r
            .AssumeExistingTopology()
            .Publish<RabbitOrderPlaced>()
            .Publish<RabbitBroadcast>(p => p.ToExchange($"{_prefix}.other")));
        var transport = Runtime(host).Transport;

        var missing = await transport.SendAsync(RabbitMqRegistrationTests.Outbound(OrderName, [1]), TestContext.Current.CancellationToken);
        var existing = await transport.SendAsync(RabbitMqRegistrationTests.Outbound("tests.rabbit.broadcast", [2]), TestContext.Current.CancellationToken);

        missing.Status.Should().Be(TransportSendStatus.Rejected);
        missing.Reason.Should().Contain("404");
        existing.Should().Be(TransportSendResult.Sent, "a channel the broker closed is not reused");
        (await fixture.ReceiveAsync(Observer)).RoutingKey.Should().Be("tests.rabbit.broadcast");
    }

    [Fact(DisplayName = "RabbitMQ publish: the transport declares its exchange idempotently, again after every reconnect")]
    public async Task The_exchange_is_declared_on_every_connection()
    {
        var proxy = Proxy();
        var host = await HostAsync(proxy, r => r.Publish<RabbitOrderPlaced>());
        await using (var channel = await fixture.ChannelAsync())
            await channel.ExchangeDeclarePassiveAsync(Exchange, TestContext.Current.CancellationToken);
        host.Logs.WithId(8010).Should().ContainSingle();

        // Deleted while the transport runs, as a broker that restarted empty would lose it: declared again on reconnect.
        await using (var channel = await fixture.ChannelAsync())
            await channel.ExchangeDeleteAsync(Exchange, cancellationToken: TestContext.Current.CancellationToken);
        proxy.Cut();
        await host.Logs.WaitForAsync(8001);
        proxy.Restore();
        await host.Logs.WaitForAsync(8002);

        await using (var channel = await fixture.ChannelAsync())
            await channel.ExchangeDeclarePassiveAsync(Exchange, TestContext.Current.CancellationToken);
        host.Logs.WithId(8010).Should().HaveCount(2);
    }

    [Fact(DisplayName = "RabbitMQ publish: during an outage the outbox defers the message without an attempt, and sends it once the broker is back")]
    public async Task An_outage_defers_then_the_message_is_sent()
    {
        var proxy = Proxy();
        await fixture.BindObserverAsync(Exchange, OrderName, Observer);
        var host = await HostAsync(proxy, r => r.Publish<RabbitOrderPlaced>());
        using var outcomes = new InstrumentRecorder<long>(host.Services, CqrsTelemetry.Instruments.OutboxMessages);
        var publisher = Runtime(host).Publisher;

        proxy.Cut();
        await host.Logs.WaitForAsync(8001);
        await host.PublishAsync(new RabbitOrderPlaced(1, "k"));
        await host.Logs.WaitForAsync(5029);
        var deferred = OutboxTestHarness.Stored(host.Services).Should().ContainSingle(m => m.HandlerName == "rabbitmq").Subject;
        deferred.AttemptCount.Should().Be(0, "an outage charges no attempt");

        proxy.Restore();
        (await fixture.ReceiveAsync(Observer)).RoutingKey.Should().Be(OrderName, "the deferred message is sent once the connection is back");
        outcomes.Measurements.Select(m => m.Tag(CqrsTelemetry.Tags.Outcome)).Should().Contain("unavailable");
        publisher.Current.Should().NotBeNull();
    }

    [Fact(DisplayName = "RabbitMQ publish: notifications of one key reach the broker in the order they were published")]
    public async Task A_partition_reaches_the_broker_in_order()
    {
        var proxy = Proxy();
        await fixture.BindObserverAsync(Exchange, OrderName, Observer);
        var host = await HostAsync(proxy, r => r.Publish<RabbitOrderPlaced>());

        await host.PublishAsync(Enumerable.Range(1, 20).Select(i => (INotification)new RabbitOrderPlaced(i, "order-42")).ToArray());

        var serializer = host.Services.GetRequiredService<INotificationSerializer>();
        var received = await fixture.ReceiveAsync(Observer, 20);

        received.Select(m => ((RabbitOrderPlaced)serializer.Deserialize(OrderName, m.Body)!).Seq).Should().Equal(Enumerable.Range(1, 20));
    }

    [Fact(DisplayName = "RabbitMQ publish: a scheduled notification reaches the broker only once it is due")]
    public async Task A_scheduled_notification_is_published_when_due()
    {
        var proxy = Proxy();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await fixture.BindObserverAsync(Exchange, "tests.rabbit.broadcast", Observer);
        var host = await HostAsync(proxy, r => r.Publish<RabbitBroadcast>(), time);

        await using (var scope = host.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().PublishAfter(new RabbitBroadcast(7), TimeSpan.FromMinutes(10), TestContext.Current.CancellationToken);

        var stored = OutboxTestHarness.Stored(host.Services).Should().ContainSingle().Subject;
        stored.HandlerName.Should().Be("rabbitmq");
        stored.NextRetryAt.Should().Be(time.GetUtcNow().UtcDateTime.AddMinutes(10), "the transport's message carries the due time");
        (await fixture.CountAsync(Observer)).Should().Be(0u, "it is not due, on a clock only the test moves");

        time.Advance(TimeSpan.FromMinutes(10));
        var message = await fixture.ReceiveAsync(Observer);
        Encoding.UTF8.GetString(message.Body).Should().Contain("7");
    }
}
