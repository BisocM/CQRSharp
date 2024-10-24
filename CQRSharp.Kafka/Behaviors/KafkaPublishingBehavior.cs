using CQRSharp.Core.Pipeline;
using CQRSharp.Interfaces.Markers.Request;
using CQRSharp.Interfaces.Markers.Command;
using Microsoft.Extensions.Logging;
using Confluent.Kafka;
using System.Reflection;
using System.Text.Json;

namespace CQRSharp.Kafka.Behaviors
{
    public class KafkaPublishingBehavior<TRequest, TResult>(
        IProducer<string, string> producer,
        KafkaOptions options,
        ILogger<KafkaPublishingBehavior<TRequest, TResult>> logger)
        : IPipelineBehavior<TRequest, TResult>
        where TRequest : RequestBase, ICommand
    {
        public async Task<TResult> Handle(TRequest request, CancellationToken cancellationToken, Func<CancellationToken, Task<TResult>> next)
        {
            if (!ShouldPublishToKafka(request)) return await next(cancellationToken);
            var commandName = typeof(TRequest).Name;
            var commandData = JsonSerializer.Serialize(request, KafkaSerializationSettings.SerializerOptions);

            var message = new Message<string, string>
            {
                Key = commandName,
                Value = commandData
            };

            await producer.ProduceAsync(options.CommandTopic, message, cancellationToken);

            logger.LogInformation("Published command {CommandName} to Kafka topic {Topic}", commandName, options.CommandTopic);

            //TODO: Make this a configurable option - whether to continue processing locally after publishing to Kafka.
            //Do not continue processing locally after publishing to Kafka.
            return default!;

            //Continue to the next behavior or handler
        }

        private bool ShouldPublishToKafka(TRequest request)
        {
            //Implement your logic to decide whether to publish the command to Kafka.
            //This could be based on an attribute, configuration, or any custom logic.
            var publishAttribute = typeof(TRequest).GetCustomAttribute<PublishToKafkaAttribute>();
            return publishAttribute != null;
        }
    }
}