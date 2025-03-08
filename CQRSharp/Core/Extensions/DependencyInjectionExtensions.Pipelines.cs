using CQRSharp.Core.Caching.Pipelines;
using CQRSharp.Core.Options;
using CQRSharp.Core.Pipelines;
using CQRSharp.Core.Pipelines.Types;
using CQRSharp.Core.Pipelines.Types.RateLimiting;
using CQRSharp.Shared.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Extensions
{
    public static partial class DependencyInjectionExtensions
    {
        // A static logger for configuration-time logging.
        private static readonly ILogger Logger = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        }).CreateLogger("DependencyInjectionExtensions");

        /// <summary>
        /// Registers the execution logging pipeline behavior in the service collection.
        /// </summary>
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

            Logger.LogInformation("ExecutionLoggingBehavior has been registered.");

            return services;
        }

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

        /// <summary>
        /// Registers the pipeline registry using AoT-generated pipeline builders.
        /// Looks for a generated type in the known namespace and uses it if available.
        /// </summary>
        private static IReadOnlyDictionary<Type, PipelineBuilderDelegate> AddPipelineRegistryUsingGeneratedPipelines(this IServiceCollection services)
        {
            try
            {
                var generatedType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType($"{SourceGeneratorConstants.GeneratedNamespace}.{SourceGeneratorConstants.PipelineRegistryClassName}"))
                    .FirstOrDefault(t => t != null);

                if (generatedType == null)
                    throw new InvalidOperationException("Generated pipeline builders type not found. Ensure the AoT generator has run.");

                var mapProperty = generatedType.GetProperty(SourceGeneratorConstants.PipelineMapPropertyName);
                if (mapProperty == null)
                    throw new InvalidOperationException($"Generated {SourceGeneratorConstants.PipelineMapPropertyName} property not found on the generated pipeline builders type.");

                if (mapProperty.GetValue(null) is not IReadOnlyDictionary<Type, PipelineBuilderDelegate> pipelineMap)
                    throw new InvalidOperationException("Generated PipelineMap property is null or of an unexpected type.");

                if (pipelineMap.Count == 0)
                    throw new InvalidOperationException("No pipeline builders were found in the generated registry.");

                services.AddSingleton<IPipelineRegistry>(new PipelineRegistry(pipelineMap));
                Logger.LogInformation("Pipeline registry registered using AoT-generated pipeline builders.");
                return pipelineMap;
            }
            catch (Exception ex)
            {
                Logger.LogCritical(ex, "Failed to register AoT-generated pipeline builders. Please ensure that the compile-time code generator has run.");
                throw;
            }
        }
    }
}