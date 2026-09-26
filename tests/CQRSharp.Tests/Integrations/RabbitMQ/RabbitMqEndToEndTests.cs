using System.Collections.Concurrent;
using System.Diagnostics;
using CQRSharp.Core.Diagnostics;
using CQRSharp.EntityFrameworkCore;
using CQRSharp.Persistence;
using CQRSharp.RabbitMQ;
using CQRSharp.Tests.Integrations.EntityFrameworkCore.Providers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CQRSharp.Tests.Integrations.RabbitMQ;

/// <summary>
///     Two applications, a broker between them, the consumer on the EF Core outbox and inbox with the EF Core unit of work
///     on PostgreSQL: a notification a request publishes in its transaction reaches the other application's handler exactly
///     once, a redelivery of it included, since the intake's messages and its dedupe record are one commit; and the trace of
///     the request that published it continues into that handler.
/// </summary>
[Collection(RabbitMqPostgreSqlCollection.Name)]
public sealed class RabbitMqEndToEndTests(RabbitMqFixture broker, RabbitMqPostgreSqlFixture database) : IAsyncLifetime
{
    private const string OrderName = "tests.rabbit.order-placed";

    private readonly string _prefix = RabbitMqFixture.NewPrefix();
    private readonly List<IAsyncDisposable> _owned = [];

    private string Exchange => $"{_prefix}.x";
    private string Queue => $"{_prefix}.q";

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
            await _owned[i].DisposeAsync();
        await broker.DeleteAsync([Queue, $"{Queue}.dead-letter", $"{_prefix}.copy"], [Exchange, $"{_prefix}.dlx"]);
    }

    private async Task<(RabbitMqTestHost Producer, RabbitMqTestHost Consumer)> StartAsync()
    {
        Assert.SkipUnless(broker.Available, broker.SkipReason);
        Assert.SkipUnless(database.Available, database.SkipReason);
        await database.ResetAsync();
        var proxy = broker.NewProxy();
        _owned.Add(proxy);

        var consumer = await RabbitMqTestHost.StartAsync(
            broker.UriThrough(proxy),
            r => r.Configure(Topology).Consume(Queue, q => q.Bind<RabbitOrderPlaced>()),
            outbox: o => o.Enabled().UseEntityFrameworkCore<ProviderTestDbContext>(),
            cqrs: b => b.UseEntityFrameworkCoreUnitOfWork<ProviderTestDbContext>(),
            services: s => s.AddDbContext<ProviderTestDbContext>(database.Configure));
        _owned.Add(consumer);
        await consumer.Logs.WaitForAsync(8020);

        var producer = await RabbitMqTestHost.StartAsync(broker.UriThrough(proxy), r => r.Configure(Topology).Publish<RabbitOrderPlaced>());
        _owned.Add(producer);
        return (producer, consumer);
    }

    private void Topology(RabbitMqTransportOptions options)
    {
        options.DefaultExchange = Exchange;
        options.DeadLetterExchange = $"{_prefix}.dlx";
    }

    private async Task<(int Outbox, int Inbox)> CountAsync()
    {
        await using var context = new ProviderTestDbContext(database.CreateOptions());
        return (await context.Set<OutboxEntity>().CountAsync(), await context.Set<InboxEntity>().CountAsync());
    }

    [Fact(DisplayName = "RabbitMQ end to end: a transactional request's notification reaches the other application's handler exactly once, a redelivery included")]
    public async Task A_notification_is_taken_in_exactly_once()
    {
        var (producer, consumer) = await StartAsync();
        // A second queue on the same binding keeps a copy of what was published, to deliver it again as a broker redelivery would.
        await broker.BindObserverAsync(Exchange, OrderName, $"{_prefix}.copy");

        await using (var scope = producer.Services.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
                .Send(new PlaceRabbitOrderCommand { Seq = 1, Key = "order-1" }, TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();

        await consumer.Received.WaitForAsync(1);
        var copy = await broker.ReceiveAsync($"{_prefix}.copy");

        // The same message again, with its message id: the intake finds its record and stores nothing.
        await broker.PublishRawAsync(Exchange, OrderName, copy.Body,
            RabbitMqFixture.Properties(copy.BasicProperties.MessageId, copy.BasicProperties.Type,
                copy.BasicProperties.Headers?.ToDictionary(h => h.Key, h => h.Value)));
        await consumer.Logs.WaitForAsync(5101);

        consumer.Received.Orders.Should().Equal(new RabbitOrderPlaced(1, "order-1"));
        var (outbox, inbox) = await CountAsync();
        outbox.Should().Be(1, "one outbox message was stored for the consumer's one handler");
        inbox.Should().Be(2, "the intake's record, committed with that message, and the handler's delivery record");
    }

    [Fact(DisplayName = "RabbitMQ end to end: the trace of the request that published a notification continues into the other application's handler")]
    public async Task The_trace_continues_across_the_broker()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CqrsTelemetry.ActivitySourceName,
            Sample = (ref _) => ActivitySamplingResult.AllData,
            ActivityStopped = spans.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        var (producer, consumer) = await StartAsync();

        ActivityTraceId traceId;
        using (var request = new Activity("placing an order").Start())
        {
            traceId = request.TraceId;
            await producer.PublishAsync(new RabbitOrderPlaced(2, "order-2"));
        }

        await consumer.Received.WaitForAsync(1);
        consumer.Received.TraceOf(2).Should().Be(traceId, "the consumer's handler runs in the trace of the request that published it");
        spans.Should().Contain(s => s.OperationName == "CQRS Transport Receive" && s.TraceId == traceId);
        spans.Should().Contain(s => s.OperationName == "CQRS Outbox Dispatch" && s.TraceId == traceId && Equals(s.GetTagItem("messaging.system"), "rabbitmq"));
    }
}
