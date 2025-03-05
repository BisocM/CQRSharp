using System.Collections.Concurrent;
using System.Reflection;
using CQRSharp.Core.Caching.Pipelines;
using CQRSharp.Core.Options;
using CQRSharp.Core.Pipelines;
using CQRSharp.Core.Pipelines.Types;
using CQRSharp.Core.Pipelines.Types.RateLimiting;
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
        private static IReadOnlyDictionary<Type, PipelineBuilderDelegate> AddPipelineRegistryUsingGeneratedPipelines(this IServiceCollection services, params Assembly[] assemblies)
        {
            try
            {
                var generatedType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType($"{SourceGeneratorConstants.GeneratedNamespace}.{SourceGeneratorConstants.PipelineRegistryClassName}"))
                    .FirstOrDefault(t => t != null);

                if (generatedType == null)
                {
                    throw new InvalidOperationException("Generated pipeline builders type not found. Ensure the AoT generator has run.");
                }

                var mapProperty = generatedType.GetProperty(SourceGeneratorConstants.PipelineMapPropertyName, BindingFlags.Public | BindingFlags.Static);
                if (mapProperty == null)
                {
                    throw new InvalidOperationException($"Generated {SourceGeneratorConstants.PipelineMapPropertyName} property not found on the generated pipeline builders type.");
                }

                if (mapProperty.GetValue(null) is not IReadOnlyDictionary<Type, PipelineBuilderDelegate> pipelineMap)
                {
                    throw new InvalidOperationException("Generated PipelineMap property is null or of an unexpected type.");
                }

                if (pipelineMap.Count == 0)
                {
                    throw new InvalidOperationException("No pipeline builders were found in the generated registry.");
                }

                services.AddSingleton<IPipelineRegistry>(new PipelineRegistry(pipelineMap));
                Logger.LogInformation("Pipeline registry registered using AoT-generated pipeline builders.");
                return pipelineMap;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to register AoT-generated pipeline builders. Falling back to reflection-based pipeline composition.");
                return services.AddPipelineRegistryUsingReflection(assemblies);
            }
        }

        /// <summary>
        /// Registers the pipeline registry using reflection-based pipeline composition.
        /// This fallback is used when AoT-generated code is not available.
        /// </summary>
        private static IReadOnlyDictionary<Type, PipelineBuilderDelegate> AddPipelineRegistryUsingReflection(this IServiceCollection services, params Assembly[] assemblies)
        {
            var pipelineMap = BuildPipelineMapReflection(assemblies, Logger);
            if (pipelineMap.Count == 0)
            {
                throw new InvalidOperationException("No pipeline builders were discovered via reflection-based pipeline composition.");
            }

            services.AddSingleton<IPipelineRegistry>(new PipelineRegistry(pipelineMap));
            Logger.LogInformation("Pipeline registry registered using reflection-based composition.");
            return pipelineMap;
        }

        /// <summary>
        /// Builds a pipeline map via reflection by scanning the provided assemblies for IRequest types.
        /// For each found request type, a PipelineBuilderDelegate is created to chain registered IPipelineBehavior.
        /// </summary>
        private static ConcurrentDictionary<Type, PipelineBuilderDelegate> BuildPipelineMapReflection(Assembly[] assemblies, ILogger logger)
        {
            var pipelineMap = new ConcurrentDictionary<Type, PipelineBuilderDelegate>();

            // Get all types that implement IRequest.
            var allRequestTypes = assemblies.SelectMany(a => a.GetTypes())
                .Where(t => typeof(Interfaces.Markers.Request.IRequest).IsAssignableFrom(t));

            foreach (var requestType in allRequestTypes)
            {
                Type resultType = GetResultTypeForRequestType(requestType);

                PipelineBuilderDelegate builder = (svc, req, finalHandler, ct) =>
                {
                    var behaviorInterfaceType = typeof(IPipelineBehavior<,>).MakeGenericType(requestType, resultType);
                    var behaviors = svc.GetServices(behaviorInterfaceType).Cast<object>().ToArray();

                    // Final delegate simply calls the provided finalHandler.
                    Func<object, CancellationToken, Task<object>> pipeline = async (_, token) => await finalHandler(token);

                    // Chain each behavior in reverse order.
                    foreach (var behavior in behaviors.Reverse())
                    {
                        var next = pipeline;
                        pipeline = (r, token) =>
                        {
                            var closedInterface = behavior.GetType().GetInterfaces()
                                .FirstOrDefault(i => i.IsGenericType &&
                                                     i.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>) &&
                                                     i.GenericTypeArguments[0] == requestType &&
                                                     i.GenericTypeArguments[1] == resultType);
                            if (closedInterface == null)
                            {
                                logger.LogError("Pipeline behavior interface not found for behavior {BehaviorType} and request {RequestType}.",
                                    behavior.GetType().FullName, requestType.FullName);
                                throw new InvalidOperationException("Pipeline behavior interface not found.");
                            }

                            var handleMethod = closedInterface.GetMethod("Handle");
                            if (handleMethod == null)
                            {
                                logger.LogError("Handle method not found on pipeline behavior {BehaviorType}.", behavior.GetType().FullName);
                                throw new InvalidOperationException("Handle method not found on pipeline behavior.");
                            }

                            logger.LogDebug("Invoking Handle method on pipeline behavior {BehaviorType} for request {RequestType}.",
                                behavior.GetType().FullName, requestType.FullName);

                            var invocationResult = handleMethod.Invoke(behavior, [
                                r,
                                new Func<CancellationToken, Task<object>>(t => next(r, t)),
                                token
                            ]);

                            if (invocationResult == null)
                            {
                                logger.LogError("Handle method for pipeline behavior {BehaviorType} returned null.",
                                    behavior.GetType().FullName);
                                throw new InvalidOperationException("Pipeline behavior returned null result.");
                            }

                            if (invocationResult is Task<object> task)
                            {
                                return task;
                            }
                            else
                            {
                                logger.LogError("Handle method for pipeline behavior {BehaviorType} did not return Task<object>.",
                                    behavior.GetType().FullName);
                                throw new InvalidOperationException("Pipeline behavior did not return a valid Task<object>.");
                            }
                        };
                    }

                    return pipeline(req, ct);
                };

                pipelineMap[requestType] = builder;
            }

            return pipelineMap;
        }
    }
}