using CQRSharp.Core.Caching.Pipelines;
using CQRSharp.Core.Options;
using CQRSharp.Core.Pipelines;
using CQRSharp.Core.Pipelines.Types;
using CQRSharp.Core.Pipelines.Types.RateLimiting;
using CQRSharp.Core.SourceGeneration;
using CQRSharp.Shared.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Extensions
{
    public static partial class DependencyInjectionExtensions
    {
        //A static logger for configuration-time logging.
        private static readonly ILogger Logger = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        }).CreateLogger("DependencyInjectionExtensions");

        /// <summary>
        /// Registers the resilience pipeline behavior in the service collection.
        /// </summary>
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

            Logger.LogInformation("ResilienceBehavior has been registered.");

            return services;
        }

        /// <summary>
        /// Registers the timeout pipeline behavior in the service collection.
        /// </summary>
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

            Logger.LogInformation("TimeoutBehavior has been registered.");

            return services;
        }

        /// <summary>
        /// Registers the rate limiting pipeline behavior in the dependency injection container.
        /// </summary>
        public static IServiceCollection AddRateLimiting(
            this IServiceCollection services,
            Action<RateLimiterOptions> configureOptions)
        {
            if (configureOptions == null)
                throw new ArgumentNullException(nameof(configureOptions), "Rate limiting configuration must be provided.");

            var config = new RateLimiterOptions();
            configureOptions(config);

            if (config.MaxTokens <= 0 || config.ReplenishRatePerSecond <= 0)
                throw new ArgumentException("Rate limiting configuration is invalid. MaxTokens and ReplenishRatePerSecond must be greater than zero.");

            services.AddSingleton(config);
            services.AddSingleton<RateLimiter>();
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(RateLimitingBehavior<,>));

            Logger.LogInformation("RateLimitingBehavior has been registered.");

            return services;
        }
    }
}