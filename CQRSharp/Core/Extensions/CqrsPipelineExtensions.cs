using CQRSharp.Core.Options;
using CQRSharp.Core.Pipelines;
using CQRSharp.Core.Pipelines.Types;
using CQRSharp.Core.Pipelines.Types.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Extensions
{
    /// <summary>
    /// Provides extension methods for adding CQRS pipeline behaviors to the dependency injection container.
    /// </summary>
    public static class CqrsPipelineExtensions
    {
        /// <summary>
        /// Registers the execution logging pipeline behavior in the service collection.
        /// </summary>
        /// <param name="services">The service collection to which the execution logging behavior should be added.</param>
        /// <param name="configureOptions">A delegate to configure the logging options.</param>
        /// <returns>An updated instance of the service collection with the execution logging behavior registered.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the <paramref name="configureOptions"/> parameter is null.</exception>
        public static IServiceCollection AddExecutionLoggingBehavior(
            this IServiceCollection services,
            Action<LoggingOptions> configureOptions)
        {
            if (configureOptions == null)
                throw new ArgumentNullException(nameof(configureOptions), "Logging configuration must be provided.");

            var options = new LoggingOptions();
            configureOptions(options);

            services.AddSingleton(options);
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ExecutionLoggingBehavior<,>));
            return services;
        }

        /// <summary>
        /// Registers the resilience pipeline behavior in the service collection.
        /// </summary>
        /// <param name="services">The service collection to which the resilience behavior should be added.</param>
        /// <param name="configureOptions">A delegate to configure the resilience options.</param>
        /// <returns>An updated instance of the service collection with the resilience behavior registered.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the <paramref name="configureOptions"/> parameter is null.</exception>
        public static IServiceCollection AddResilienceBehavior(
            this IServiceCollection services,
            Action<ResilienceOptions> configureOptions)
        {
            if (configureOptions == null)
                throw new ArgumentNullException(nameof(configureOptions), "Resilience configuration must be provided.");

            var options = new ResilienceOptions();
            configureOptions(options);

            services.AddSingleton(options);
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ResilienceBehavior<,>));
            return services;
        }

        /// <summary>
        /// Registers the timeout pipeline behavior in the service collection.
        /// </summary>
        /// <param name="services">The service collection to which the timeout behavior should be added.</param>
        /// <param name="configureOptions">A delegate to configure the timeout options.</param>
        /// <returns>An updated instance of the service collection with the timeout behavior registered.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the <paramref name="configureOptions"/> parameter is null.</exception>
        public static IServiceCollection AddTimeoutBehavior(
            this IServiceCollection services,
            Action<TimeoutOptions> configureOptions)
        {
            if (configureOptions == null)
                throw new ArgumentNullException(nameof(configureOptions), "Timeout configuration must be provided.");

            var options = new TimeoutOptions();
            configureOptions(options);

            services.AddSingleton(options);
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(TimeoutBehavior<,>));
            return services;
        }

        /// <summary>
        /// Registers the rate limiting pipeline behavior in the application's dependency injection container.
        /// </summary>
        /// <param name="services">The service collection to which the rate limiting services are added.</param>
        /// <param name="configureOptions">A delegate to configure the rate limiting options.</param>
        /// <returns>The same service collection to allow method chaining.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the <paramref name="configureOptions"/> or its resultant configuration is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the provided configuration has invalid values such as non-positive MaxTokens or ReplenishRatePerSecond.</exception>
        public static IServiceCollection AddRateLimiting(
            this IServiceCollection services,
            Action<RateLimiterOptions> configureOptions)
        {
            if (configureOptions == null)
                throw new ArgumentNullException(nameof(configureOptions),
                    "Rate limiting configuration must be provided.");

            //Create a new instance that the consumer can configure.
            var config = new RateLimiterOptions();
            configureOptions(config);

            if (config.MaxTokens <= 0 || config.ReplenishRatePerSecond <= 0)
                throw new ArgumentException(
                    "Rate limiting configuration is invalid. MaxTokens and ReplenishRatePerSecond must be greater than zero.");

            services.AddSingleton(config);
            services.AddSingleton<RateLimiter>();
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(RateLimitingBehavior<,>));

            return services;
        }
    }
}