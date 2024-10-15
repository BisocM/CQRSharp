using Confluent.Kafka;
using CQRSharp.Core.Notifications;
using CQRSharp.Kafka.Dispatcher;
using CQRSharp.Kafka.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Kafka.Extensions
{
    public static class DependencyInjectionExtensions
    {
        /// <summary>
        /// Adds the necessary services for integrating CQRS with Kafka to the specified <see cref="IServiceCollection"/>.
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> to add the services to.</param>
        /// <param name="configureKafka">An action to configure the <see cref="KafkaOptions"/>.</param>
        /// <returns>The <see cref="IServiceCollection"/> so that additional calls can be chained.</returns>
        public static IServiceCollection AddKafka(this IServiceCollection services,
            Action<KafkaOptions>? configureKafka)
        {
            //Configure the Kafka options with the user's configuration.
            var kafkaOptions = new KafkaOptions();
            configureKafka?.Invoke(kafkaOptions);
            services.AddSingleton(kafkaOptions);

            //Initialize the producer with the options configured by the user earlier.
            var producerConfig = new ProducerConfig { BootstrapServers = kafkaOptions.BootstrapServers };
            services.AddSingleton(new ProducerBuilder<string, string>(producerConfig).Build());

            //Add the Kafka dispatcher to the service collection.
            services.AddHostedService<KafkaConsumerService>();
            services.AddSingleton<NotificationDispatcher, KafkaNotificationDispatcher>();
            
            return services;
        }
    }
}
