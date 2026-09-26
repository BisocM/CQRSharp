using CQRSharp.RabbitMQ;
using CQRSharp.Testing;
using CQRSharp.Transports;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Xunit;

namespace CQRSharp.Tests.Integrations.RabbitMQ;

/// <summary>
///     The RabbitMQ transport against the transport contract suite, on a real broker: what it sends arrives on a queue bound
///     to its exchange, read back as a consumer outside CQRSharp would; the outage is a cut of the proxy it connects through.
/// </summary>
[Collection(RabbitMqCollection.Name)]
public sealed class RabbitMqTransportContractTests(RabbitMqFixture fixture) : NotificationTransportContractTests
{
    private readonly string _prefix = RabbitMqFixture.NewPrefix();
    private TcpProxy? _proxy;
    private ServiceProvider? _provider;
    private string? _queue;

    private string Exchange => $"{_prefix}.x";

    protected override async Task<INotificationTransport> CreateTransportAsync(string notificationName)
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        _proxy = fixture.NewProxy();
        _queue = await fixture.BindObserverAsync(Exchange, notificationName, $"{_prefix}.observer");

        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.UseInMemoryStore().UseRabbitMq(fixture.UriThrough(_proxy), r => r
            .Configure(options =>
            {
                options.DefaultExchange = Exchange;
                options.PublishTimeout = TimeSpan.FromSeconds(5);
                options.ReconnectMaxDelay = TimeSpan.FromSeconds(1);
            })
            .Publish(notificationName))));
        _provider = services.BuildServiceProvider();

        var runtime = _provider.GetRequiredKeyedService<RabbitMqTransportRuntime>(RabbitMqTransportOptions.DefaultTransportName);
        await runtime.Publisher.WaitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        return runtime.Transport;
    }

    protected override async Task<ReceivedTransportMessage?> ReceiveAsync(TimeSpan timeout)
    {
        if (await fixture.GetAsync(_queue!, timeout) is not { } message) return null;

        var properties = message.BasicProperties;
        return new ReceivedTransportMessage(
            Guid.TryParse(properties.MessageId, out var id) ? id : null,
            properties.Type ?? string.Empty,
            message.Body.ToArray(),
            RabbitMqFixture.Header(properties, "cqrsharp-partition-key"),
            RabbitMqFixture.Header(properties, "traceparent"));
    }

    protected override async Task<bool> MakeUnavailableAsync()
    {
        _proxy!.Cut();
        var publisher = _provider!.GetRequiredKeyedService<RabbitMqTransportRuntime>(RabbitMqTransportOptions.DefaultTransportName).Publisher;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (publisher.Current is not null && DateTime.UtcNow < deadline)
            await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
        return publisher.Current is null;
    }

    protected override Task RestoreAvailabilityAsync()
    {
        _proxy!.Restore();
        return Task.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        if (_proxy is not null) await _proxy.DisposeAsync();
        await fixture.DeleteAsync([$"{_prefix}.observer"], [Exchange]);
    }
}
