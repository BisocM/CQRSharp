using CQRSharp.RabbitMQ;
using CQRSharp.Sample.Application.Commands.Requests;
using CQRSharp.Sample.Domain.Events;
using CQRSharp.Sample.Infrastructure.Logging;
using CQRSharp.Sample.Presentation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace CQRSharp.Sample.Infrastructure.SelfTest;

// The RabbitMQ transport, in two parts. The registration always: the same application with the transport, built and
// validated but never started, so nothing connects and the scenario runs anywhere. The round trip when a broker is given
// (CQRSHARP_TEST_RABBITMQ, an AMQP URI): one application dispatches a transactional command whose integration event the
// outbox publishes to RabbitMQ, and a second application takes it in from its queue and delivers it to its handler. The
// Native AOT binary therefore publishes and consumes, not only compiles the transport.
public sealed partial class SelfTestRunner
{
    private const string BrokerVariable = "CQRSHARP_TEST_RABBITMQ";
    private static readonly TimeSpan BrokerTimeout = TimeSpan.FromSeconds(30);

    private async Task RunRabbitMqTransportTestAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        await RunRabbitMqRegistrationAsync(cancellationToken);

        var broker = Environment.GetEnvironmentVariable(BrokerVariable);
        if (string.IsNullOrWhiteSpace(broker))
        {
            SampleLog.RabbitMqRoundTripSkipped(logger, BrokerVariable);
            return;
        }

        await RunRabbitMqRoundTripAsync(broker, cancellationToken);
    }

    private static async Task RunRabbitMqRegistrationAsync(CancellationToken cancellationToken)
    {
        using var host = BuildRabbitMqApplication("amqp://guest:guest@127.0.0.1:5672/", r => r
            .Publish<ShipmentDispatchedNotification>()
            .Consume("sample.shipments", q => q.Bind<ShipmentDispatchedNotification>().SingleActiveConsumer()));
        host.Services.GetRequiredService<IStartupValidator>().Validate();

        // Resolving the health check builds the transport, which opens nothing until the host starts.
        var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(cancellationToken);
        Require(report.Entries["cqrsharp.rabbitmq"].Description?.Contains("the publish connection is not open", StringComparison.Ordinal) == true,
            $"The RabbitMQ health check of a host that never started reported '{report.Entries["cqrsharp.rabbitmq"].Description}'.");
    }

    private static async Task RunRabbitMqRoundTripAsync(string broker, CancellationToken cancellationToken)
    {
        // Names of this run's own, so runs sharing a broker never meet.
        var prefix = $"sample-{Guid.NewGuid():N}";
        var queue = $"{prefix}.shipments";
        void Topology(RabbitMqTransportOptions options)
        {
            options.DefaultExchange = $"{prefix}.notifications";
            options.DeadLetterExchange = $"{prefix}.dead-letter";
        }

        using var consumer = BuildRabbitMqApplication(broker, r => r.Configure(Topology).Consume(queue, q => q.Bind<ShipmentDispatchedNotification>()));
        using var producer = BuildRabbitMqApplication(broker, r => r.Configure(Topology).Publish<ShipmentDispatchedNotification>());
        try
        {
            // The consumer declares its queue and binding; published before, the event would find no queue and be retried.
            await consumer.StartAsync(cancellationToken);
            await WaitUntilHealthyAsync(consumer.Services, cancellationToken);
            await producer.StartAsync(cancellationToken);

            var shipment = new ShipmentDispatchedNotification(Guid.NewGuid(), Guid.NewGuid(), "sample-carrier");
            await using (var scope = producer.Services.CreateAsyncScope())
            {
                var result = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
                    .Send(new DispatchShipmentCommand(shipment.ShipmentId, shipment.OrderId, shipment.Carrier), cancellationToken);
                Require(result.IsSuccess, "DispatchShipmentCommand did not succeed.");
            }

            var received = await consumer.Services.GetRequiredService<SampleDiagnostics>()
                .WaitForShipmentDispatchedAsync(shipment.ShipmentId, BrokerTimeout, cancellationToken);
            Require(received == shipment, $"The consuming application received {received}, not what was published.");

            // A notification sent to RabbitMQ still reaches the publishing application's own handler.
            var local = await producer.Services.GetRequiredService<SampleDiagnostics>()
                .WaitForShipmentDispatchedAsync(shipment.ShipmentId, BrokerTimeout, cancellationToken);
            Require(local == shipment, $"The publishing application's handler received {local}.");
        }
        finally
        {
            await producer.StopAsync(CancellationToken.None);
            await consumer.StopAsync(CancellationToken.None);
            await DeleteTopologyAsync(broker, [queue, $"{queue}.dead-letter"], [$"{prefix}.notifications", $"{prefix}.dead-letter"]);
        }
    }

    // The sample application with the RabbitMQ transport added to its outbox, and the transport's health check.
    private static IHost BuildRabbitMqApplication(string broker, Action<RabbitMqTransportBuilder> transport)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.ConfigureContainer(new DefaultServiceProviderFactory(SampleApplication.ValidatingProviderOptions));
        builder.Services.AddSampleApplication();
        builder.Services.AddCqrsGenerated(b => b.UseOutbox(o => o.UseRabbitMq(broker, transport)));
        builder.Services.AddHealthChecks().AddCqrsRabbitMq();
        return builder.Build();
    }

    private static async Task WaitUntilHealthyAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var health = services.GetRequiredService<HealthCheckService>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BrokerTimeout);
        HealthReport report;
        while ((report = await health.CheckHealthAsync(timeout.Token)).Status != HealthStatus.Healthy)
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
        Require(report.Entries["cqrsharp.rabbitmq"].Data.Count > 0, "The RabbitMQ health check reported no connection.");
    }

    private static async Task DeleteTopologyAsync(string broker, string[] queues, string[] exchanges)
    {
        await using var connection = await new ConnectionFactory { Uri = new Uri(broker) }.CreateConnectionAsync("cqrsharp-sample/cleanup");
        await using var channel = await connection.CreateChannelAsync();
        foreach (var queue in queues)
            await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false);
        foreach (var exchange in exchanges)
            await channel.ExchangeDeleteAsync(exchange);
    }
}
