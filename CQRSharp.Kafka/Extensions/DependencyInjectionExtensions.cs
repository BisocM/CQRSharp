using Confluent.Kafka;
using CQRSharp.Core.Pipeline;
using CQRSharp.Kafka.Behaviors;
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
        /// <returns>The <see cref="IServiceCollection"/> so that additional calls can be chained.</returns>
        public static IServiceCollection AddKafkaSupport(this IServiceCollection services, Action<KafkaOptions> configureOptions)
        {
            //Configure Kafka options
            var options = new KafkaOptions();
            configureOptions(options);
            services.AddSingleton(options);

            //Register Kafka producer
            services.AddSingleton<IProducer<string, string>>(provider =>
            {
                var kafkaOptions = provider.GetRequiredService<KafkaOptions>();
                var producerConfig = new ProducerConfig { BootstrapServers = kafkaOptions.BootstrapServers };
                return new ProducerBuilder<string, string>(producerConfig).Build();
            });

            //Register the Kafka publishing behavior
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(KafkaPublishingBehavior<,>));

            //Register Kafka consumer hosted service, ensuring IHandlerRegistry is available
            services.AddHostedService<KafkaCommandConsumerService>();

            return services;
        }
    }
}