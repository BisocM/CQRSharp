using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CQRSharp.Core.Dispatch;
using CQRSharp.Interfaces.Markers.Command;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Kafka.Services
{
    public class KafkaCommandConsumerService : BackgroundService
    {
        private readonly IConsumer<string, string> _consumer;
        private readonly IServiceProvider _serviceProvider;
        private readonly KafkaOptions _options;
        private readonly IHandlerRegistry _handlerRegistry;
        private readonly ILogger<KafkaCommandConsumerService> _logger;

        public KafkaCommandConsumerService(
            KafkaOptions options,
            IServiceProvider serviceProvider,
            IHandlerRegistry handlerRegistry,
            ILogger<KafkaCommandConsumerService> logger)
        {
            _options = options;
            _serviceProvider = serviceProvider;
            _handlerRegistry = handlerRegistry;
            _logger = logger;

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
                try
                {
                    var consumeResult = _consumer.Consume(stoppingToken);
                    if (consumeResult == null) continue;
                    var commandName = consumeResult.Message.Key;
                    var commandData = consumeResult.Message.Value;

                    var commandType = GetCommandTypeByName(commandName);

                    if (commandType != null)
                    {
                        if (JsonSerializer.Deserialize(commandData, commandType, KafkaSerializationSettings.SerializerOptions) is ICommand command)
                        {
                            using var scope = _serviceProvider.CreateScope();
                            var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
                            await dispatcher.ExecuteCommand(command, stoppingToken);
                        }
                        else
                            _logger.LogWarning("Failed to deserialize command {CommandName}", commandName);
                    }
                    else
                        _logger.LogWarning("No command type found for command {CommandName}", commandName);
                }
                catch (ConsumeException ex)
                {
                    _logger.LogError(ex, "Kafka consume exception");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled exception in KafkaCommandConsumerService");
                }
            }

            //Clean up
            _consumer.Close();
        }

        //Use the handler registry to find the command type by name
        private Type? GetCommandTypeByName(string commandName) =>
            _handlerRegistry.GetCommandTypeByName(commandName);
    }
}