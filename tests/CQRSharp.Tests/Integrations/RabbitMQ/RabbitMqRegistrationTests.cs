using CQRSharp.Core.Transports;
using CQRSharp.RabbitMQ;
using CQRSharp.Tests.Core;
using CQRSharp.Transports;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace CQRSharp.Tests.Integrations.RabbitMQ;

/// <summary>
///     What registering a RabbitMQ transport does, without a broker: the services, the named options and their validation
///     (in every environment, <c>CQRCONF013</c> included), what the transport declares to the configuration checks, what it
///     routes, and how a send ends when the broker cannot be reached or the message can never be published.
/// </summary>
public sealed class RabbitMqRegistrationTests
{
    // Nothing listens on port 1: the transport's connection loop keeps failing, as against a broker that is down.
    private const string Unreachable = "amqp://guest:guest@127.0.0.1:1/";

    private static ServiceProvider Build(Action<RabbitMqTransportBuilder> transport, bool outbox = true, Action<IServiceCollection>? more = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<RabbitReceived>();
        services.AddCqrsGenerated(b =>
        {
            if (outbox) b.UseOutbox(o => o.UseInMemoryStore().UseRabbitMq(Unreachable, transport));
        });
        if (!outbox) services.AddCqrsRabbitMq(Unreachable, transport);
        more?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static RabbitMqTransportOptions Options(IServiceProvider provider, string name = RabbitMqTransportOptions.DefaultTransportName)
        => provider.GetRequiredService<IOptionsMonitor<RabbitMqTransportOptions>>().Get(name);

    private static IEnumerable<string> Failures(Action<RabbitMqTransportBuilder> transport, bool outbox = true, string name = RabbitMqTransportOptions.DefaultTransportName)
    {
        using var provider = Build(transport, outbox);
        var act = () => Options(provider, name);
        return act.Should().Throw<OptionsValidationException>().Which.Failures;
    }

    [Fact(DisplayName = "RabbitMQ registration: UseRabbitMq registers the transport, a connector and one consumer per queue, with the defaults")]
    public async Task UseRabbitMq_registers_the_transport()
    {
        await using var provider = Build(r => r
            .Publish<RabbitOrderPlaced>()
            .Consume("billing", q => q.Bind<RabbitOrderPlaced>())
            .Consume("audit", q => q.Bind("orders.#")));

        var transport = provider.GetServices<INotificationTransport>().Should().ContainSingle().Subject;
        transport.Name.Should().Be("rabbitmq");
        provider.GetServices<IHostedService>().Count(s => s.GetType().Name == "RabbitMqConsumerService").Should().Be(2);
        provider.GetServices<IHostedService>().Should().Contain(s => s.GetType().Name == "RabbitMqConnector");

        var options = Options(provider);
        options.DefaultExchange.Should().Be("cqrsharp.notifications");
        options.DeadLetterExchange.Should().Be("cqrsharp.dead-letter");
        options.DeclareTopology.Should().BeTrue();
        options.PublishTimeout.Should().Be(TimeSpan.FromSeconds(30));
        options.MaxMessageSize.Should().Be(16 * 1024 * 1024);
        options.ContentType.Should().Be("application/json");
        options.ReconnectMaxDelay.Should().Be(TimeSpan.FromSeconds(30));
        var billing = options.Consumers.Should().ContainSingle(c => c.Queue == "billing").Subject;
        billing.Prefetch.Should().Be(32);
        billing.Lanes.Should().Be(4);
        billing.Classic.Should().BeFalse();
        billing.DeliveryLimit.Should().Be(20);
        billing.DeadLetterQueue.Should().BeTrue();
        billing.SingleActiveConsumer.Should().BeFalse();
        billing.MaxHold.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact(DisplayName = "RabbitMQ registration: every connection form of UseRabbitMq and AddCqrsRabbitMq registers a transport")]
    public async Task Every_connection_form_registers()
    {
        var connection = new Moq.Mock<IConnection>().Object;
        var factory = new ConnectionFactory();
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o
            .UseInMemoryStore()
            .UseRabbitMq(Unreachable, r => r.Name("uri"))
            .UseRabbitMq(connection, r => r.Name("given"))
            .UseRabbitMq(_ => connection, r => r.Name("given-factory"))
            .UseRabbitMq(_ => (IConnectionFactory)factory, r => r.Name("factory"))));
        services.AddCqrsRabbitMq(Unreachable, r => r.Name("uri-2"));
        services.AddCqrsRabbitMq(connection, r => r.Name("given-2"));
        services.AddCqrsRabbitMq(_ => connection, r => r.Name("given-factory-2"));
        services.AddCqrsRabbitMq(_ => (IConnectionFactory)factory, r => r.Name("factory-2"));
        await using var provider = services.BuildServiceProvider();

        provider.GetServices<INotificationTransport>().Select(t => t.Name).Should().BeEquivalentTo(
            "uri", "given", "given-factory", "factory", "uri-2", "given-2", "given-factory-2", "factory-2");
        provider.GetRequiredService<NotificationTransportRegistry>().Failure.Should().BeNull();
    }

    [Theory(DisplayName = "RabbitMQ registration: a connection URI that is not an AMQP URI is refused at once")]
    [InlineData("http://rabbit:5672/")]
    [InlineData("rabbit:5672")]
    [InlineData(" ")]
    public void A_non_amqp_uri_is_refused(string uri)
    {
        var act = () => new ServiceCollection().AddCqrsRabbitMq(uri);

        act.Should().Throw<ArgumentException>();
    }

    [Theory(DisplayName = "RabbitMQ registration: a transport name must be non-empty and fit an outbox handler name")]
    [InlineData("")]
    [InlineData(null)]
    public void A_transport_name_is_validated(string? name)
    {
        var act = () => new ServiceCollection().AddCqrsRabbitMq(Unreachable, r => r.Name(name ?? new string('n', 257)));

        act.Should().Throw<ArgumentException>();
    }

    [Fact(DisplayName = "RabbitMQ registration: a transport without the outbox fails validation with CQRCONF013, in every environment")]
    public void A_transport_without_the_outbox_fails_with_CQRCONF013()
        => Failures(r => r.Publish<RabbitOrderPlaced>(), outbox: false).Should().ContainSingle()
            .Which.Should().Contain("CQRCONF013").And.Contain("'rabbitmq'");

    [Fact(DisplayName = "RabbitMQ registration: settings RabbitMQ or the transport cannot run with fail validation, each with its reason")]
    public void Invalid_settings_fail_validation()
    {
        var failures = Failures(r => r
            .Configure(o =>
            {
                o.DefaultExchange = new string('x', 256);
                o.DeadLetterExchange = " ";
                o.PublishTimeout = TimeSpan.Zero;
                o.MaxMessageSize = 0;
                o.ReconnectMaxDelay = TimeSpan.FromMilliseconds(500);
                o.ContentType = "";
            })
            .Publish("orders.placed", p => p.ToExchange(new string('e', 300)))
            .Consume("billing", q => q.Prefetch(0).Lanes(65).MaxHold(TimeSpan.FromMinutes(30)).DeliveryLimit(0))
            .Consume("billing", q => q.Lanes(0))
            .Consume("classic", q => q.Classic().DeliveryLimit(5))
            .Consume("held", q => q.MaxHold(TimeSpan.Zero))).ToArray();

        failures.Should().Contain(f => f.Contains("DefaultExchange") && f.Contains("255 bytes"));
        failures.Should().Contain(f => f.Contains("DeadLetterExchange must not be empty"));
        failures.Should().Contain(f => f.Contains("PublishTimeout"));
        failures.Should().Contain(f => f.Contains("MaxMessageSize"));
        failures.Should().Contain(f => f.Contains("ReconnectMaxDelay"));
        failures.Should().Contain(f => f.Contains("ContentType"));
        failures.Should().Contain(f => f.Contains("the exchange of the publication of 'orders.placed'"));
        failures.Should().Contain(f => f.Contains("the prefetch of queue 'billing'"));
        failures.Should().Contain(f => f.Contains("the lanes of queue 'billing'"));
        failures.Should().Contain(f => f.Contains("the MaxHold of queue 'billing'"));
        failures.Should().Contain(f => f.Contains("the delivery limit of queue 'billing'"));
        failures.Should().Contain(f => f.Contains("'billing' is consumed twice"));
        failures.Should().Contain(f => f.Contains("'classic' is a classic queue, which has no delivery limit"));
        failures.Should().Contain(f => f.Contains("the MaxHold of queue 'held'"));
        failures.Should().OnlyContain(f => f.StartsWith("RabbitMQ transport 'rabbitmq':"));
    }

    [Fact(DisplayName = "RabbitMQ registration: a transport name and a queue together too long for the inbox's source name fail validation")]
    public void Source_name_length_is_validated()
        => Failures(r => r.Name(new string('n', 200)).Consume(new string('q', 60), _ => { }), name: new string('n', 200))
            .Should().Contain(f => f.Contains("longer than the 255 characters"));

    [Fact(DisplayName = "RabbitMQ registration: the transport declares what it publishes and consumes, exact names only")]
    public async Task The_declaration_names_what_is_configured()
    {
        await using var provider = Build(r => r
            .Publish<RabbitOrderPlaced>()
            .Publish("inventory.adjusted")
            .Consume("billing", q => q.Bind<RabbitOrderPlaced>().Bind("orders.*").Bind("payments.#").Bind("inventory.adjusted", exchange: "stock")));

        var declaration = provider.GetRequiredService<INotificationTransport>().Declaration;

        declaration.PublishedTypes.Should().Equal(typeof(RabbitOrderPlaced));
        declaration.PublishedNames.Should().Equal("inventory.adjusted");
        declaration.ConsumedTypes.Should().Equal(typeof(RabbitOrderPlaced));
        declaration.ConsumedNames.Should().Equal("inventory.adjusted");
    }

    [Fact(DisplayName = "RabbitMQ registration: the transport routes the notifications it publishes, by type or by name, and no other; the last configuration wins")]
    public async Task Routes_what_it_publishes()
    {
        await using var provider = Build(r => r
            .Publish<RabbitOrderPlaced>(p => p.WithRoutingKey("first"))
            .Publish<RabbitOrderPlaced>(p => p.WithRoutingKey("second"))
            .Publish("inventory.adjusted"));
        var transport = (RabbitMqNotificationTransport)provider.GetRequiredService<INotificationTransport>();

        transport.Routes("tests.rabbit.order-placed", typeof(RabbitOrderPlaced)).Should().BeTrue();
        transport.Routes("inventory.adjusted", typeof(INotification)).Should().BeTrue();
        transport.Routes("tests.rabbit.broadcast", typeof(RabbitBroadcast)).Should().BeFalse();
        provider.GetRequiredKeyedService<RabbitMqTransportRuntime>("rabbitmq").Publications["tests.rabbit.order-placed"]
            .Should().Be(new ResolvedPublication("cqrsharp.notifications", "second", false));
    }

    [Fact(DisplayName = "RabbitMQ send: without a connection the send is Unavailable, with a retry delay, and never waits for the broker")]
    public async Task A_send_without_a_connection_is_unavailable()
    {
        await using var provider = Build(r => r.Publish<RabbitOrderPlaced>());
        var transport = provider.GetRequiredService<INotificationTransport>();

        var result = await transport.SendAsync(Outbound("tests.rabbit.order-placed", [1]), TestContext.Current.CancellationToken);

        result.Status.Should().Be(TransportSendStatus.Unavailable);
        result.RetryAfter.Should().BePositive();
        result.Reason.Should().Contain("not open");
    }

    [Fact(DisplayName = "RabbitMQ send: a payload above MaxMessageSize, or a name no longer published, is rejected permanently before any connection")]
    public async Task Messages_that_can_never_be_published_are_rejected_permanently()
    {
        await using var provider = Build(r => r.Publish<RabbitOrderPlaced>().Configure(o => o.MaxMessageSize = 8));
        var transport = provider.GetRequiredService<INotificationTransport>();

        var large = await transport.SendAsync(Outbound("tests.rabbit.order-placed", new byte[9]), TestContext.Current.CancellationToken);
        var unknown = await transport.SendAsync(Outbound("tests.rabbit.no-longer-published", [1]), TestContext.Current.CancellationToken);

        large.Should().Match<TransportSendResult>(r => r.Status == TransportSendStatus.Rejected && r.Permanent);
        large.Reason.Should().Contain("9 bytes");
        unknown.Should().Match<TransportSendResult>(r => r.Status == TransportSendStatus.Rejected && r.Permanent);
    }

    [Fact(DisplayName = "RabbitMQ send: a send whose token is cancelled throws instead of reporting an outcome")]
    public async Task A_cancelled_send_throws()
    {
        await using var provider = Build(r => r.Publish<RabbitOrderPlaced>());
        var transport = provider.GetRequiredService<INotificationTransport>();

        var act = () => transport.SendAsync(Outbound("tests.rabbit.order-placed", [1]), new CancellationToken(canceled: true));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact(DisplayName = "RabbitMQ health: a transport whose connection is not open is unhealthy, naming it")]
    public async Task The_health_check_reports_a_closed_connection()
    {
        await using var provider = Build(r => r.Publish<RabbitOrderPlaced>(), more: s => s.AddHealthChecks().AddCqrsRabbitMq());

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);

        var entry = report.Entries["cqrsharp.rabbitmq"];
        entry.Status.Should().Be(HealthStatus.Unhealthy);
        entry.Description.Should().Contain("rabbitmq: the publish connection is not open");
        entry.Data["rabbitmq.publish"].Should().Be("closed");
    }

    [Fact(DisplayName = "RabbitMQ health: with no transport registered the check reports the failure status")]
    public async Task The_health_check_without_a_transport()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks().AddCqrsRabbitMq(failureStatus: HealthStatus.Degraded);
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);

        report.Entries["cqrsharp.rabbitmq"].Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact(DisplayName = "RabbitMQ registration: a provider disposed synchronously closes the transport without throwing")]
    public void A_synchronous_dispose_is_clean()
    {
        var provider = Build(r => r.Publish<RabbitOrderPlaced>().Consume("billing", q => q.Bind<RabbitOrderPlaced>()));
        provider.GetRequiredKeyedService<RabbitMqTransportRuntime>("rabbitmq").Publisher.Start();

        var act = () => provider.Dispose();

        act.Should().NotThrow();
    }

    internal static OutboundNotification Outbound(string name, byte[] payload)
        => new(Guid.NewGuid(), Guid.NewGuid(), name, payload, DateTime.UtcNow, null, null, null, 0);
}
