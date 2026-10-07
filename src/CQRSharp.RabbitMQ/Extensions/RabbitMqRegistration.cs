using CQRSharp.Transports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CQRSharp.RabbitMQ;

/// <summary>
///     The one registration of a RabbitMQ transport, whichever verb registers it: its named, validated options; its runtime
///     (keyed by its name, so several transports never share one); the transport the outbox forwards through; a hosted
///     service that opens its publisher connection as the host starts; and one hosted service per queue it consumes.
/// </summary>
internal static class RabbitMqRegistration
{
    public static void Register(IServiceCollection services, RabbitMqConnectionSource source, RabbitMqTransportBuilder builder)
    {
        var name = builder.TransportName;

        services.AddOptions<RabbitMqTransportOptions>(name).Configure(builder.Apply).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RabbitMqTransportOptions>, RabbitMqTransportOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);

        services.AddKeyedSingleton(name, (provider, _) => new RabbitMqTransportRuntime(name, source, provider));
        // The same runtime, for the health check, which reports every transport.
        services.AddSingleton(provider => provider.GetRequiredKeyedService<RabbitMqTransportRuntime>(name));
        services.AddSingleton<INotificationTransport>(provider => provider.GetRequiredKeyedService<RabbitMqTransportRuntime>(name).Transport);

        services.AddSingleton<IHostedService>(provider => new RabbitMqConnector(provider.GetRequiredKeyedService<RabbitMqTransportRuntime>(name)));
        foreach (var queue in builder.ConsumedQueues)
            services.AddSingleton<IHostedService>(provider => provider.GetRequiredKeyedService<RabbitMqTransportRuntime>(name).CreateConsumer(queue));
    }

    /// <summary>Opens the publisher connection as the host starts, rather than at the first send.</summary>
    private sealed class RabbitMqConnector(RabbitMqTransportRuntime runtime) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            runtime.Publisher.Start();
            return Task.CompletedTask;
        }

        // The connections stay open until the provider is disposed: the outbox processor, which may stop after this, still
        // finishes the sends it has in flight.
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
