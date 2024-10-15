using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CQRSharp.Core.Dispatch;
using CQRSharp.Interfaces.Markers.Command;

namespace CQRSharp.Kafka.Services
{
    public sealed class KafkaConsumerService : BackgroundService
    {
        private readonly IConsumer<string, string> _consumer;
        private readonly IServiceProvider _serviceProvider;
        private readonly KafkaOptions _options;

        public KafkaConsumerService(KafkaOptions options, IServiceProvider serviceProvider)
        {
            _options = options;
            _serviceProvider = serviceProvider;

            var config = new ConsumerConfig
            {
                GroupId = "command-consumer-group",
                BootstrapServers = _options.BootstrapServers,
                AutoOffsetReset = AutoOffsetReset.Earliest
            };

            _consumer = new ConsumerBuilder<string, string>(config).Build();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _consumer.Subscribe(_options.CommandTopic);

            while (!stoppingToken.IsCancellationRequested)
            {
                var consumeResult = _consumer.Consume(stoppingToken);

                //Process the command
                var command = JsonSerializer.Deserialize<ICommand>(consumeResult.Message.Value);
                using var scope = _serviceProvider.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
                if (command != null) await dispatcher.ExecuteCommand(command, stoppingToken);
            }
        }

    }
}