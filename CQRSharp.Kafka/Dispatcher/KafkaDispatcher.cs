using System.Text.Json;
using System.Windows.Input;
using Confluent.Kafka;

namespace CQRSharp.Kafka.Dispatcher
{
    public class KafkaDispatcher
    {
        private readonly IProducer<string, string> _producer;
        private readonly KafkaOptions _options;

        public KafkaDispatcher(KafkaOptions options)
        {
            _options = options;

            var config = new ProducerConfig { BootstrapServers = _options.BootstrapServers };
            _producer = new ProducerBuilder<string, string>(config).Build();
        }

        public async Task PublishCommandAsync(ICommand command, CancellationToken cancellationToken)
        {
            var commandName = command.GetType().Name;
            var commandData = JsonSerializer.Serialize(command);

            await _producer.ProduceAsync(_options.CommandTopic, new Message<string, string>
            {
                Key = commandName,
                Value = commandData
            }, cancellationToken);
        }
    }
}