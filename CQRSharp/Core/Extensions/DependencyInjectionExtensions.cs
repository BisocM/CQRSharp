using System.Collections.Concurrent;
using System.Reflection;
using CQRSharp.Core.BackgroundTasks;
using CQRSharp.Core.Caching.Pipelines;
using CQRSharp.Core.Caching.Requests;
using CQRSharp.Core.Dispatch;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Options;
using CQRSharp.Core.Pipelines;
using CQRSharp.Core.Pipelines.Attributes;
using CQRSharp.Core.Pipelines.Attributes.Markers;
using CQRSharp.Data.Commands;
using CQRSharp.Interfaces.Handlers;
using CQRSharp.Interfaces.Markers.Command;
using CQRSharp.Interfaces.Markers.Query;
using CQRSharp.Interfaces.Markers.Request;
using CQRSharp.Interfaces.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Extensions
{
    /// <summary>
    /// Provides extension methods for configuring and integrating CQRS functionality
    /// with Microsoft.Extensions.DependencyInjection.
    /// </summary>
    public static partial class DependencyInjectionExtensions
    {
        /// <summary>
        /// Adds the dispatcher and command handlers to the service collection.
        /// Responsible for automatic registration of all ICommand, IQuery{TResult},
        /// INotification, and IPipelineBehavior{TRequest, TResult} implementations.
        /// </summary>
        public static IServiceCollection AddCqrs(this IServiceCollection services,
            Action<DispatcherOptions>? configureOptions, params Assembly?[] assemblies)
        {
            Logger.LogInformation("Starting CQRS service registration.");

            // Create and register DispatcherOptions.
            var options = new DispatcherOptions();
            configureOptions?.Invoke(options);
            services.AddSingleton(options);

            // Register default components.
            services.AddTransient<IRequestContextFactory, DefaultRequestContextFactory>();
            services.AddSingleton<IDispatcher, Dispatcher>();
            services.AddSingleton<NotificationDispatcher>();
            services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
            services.AddHostedService<BackgroundTaskService>();

            // Configure logging.
            services.AddLogging(loggingBuilder =>
            {
                loggingBuilder.AddConsole();
                loggingBuilder.SetMinimumLevel(LogLevel.Information);
            });

            // Automatically register handlers and pipeline behaviors.
            var handlerMappings = RegisterAndBuildMetadata(services, assemblies);
            services.AddSingleton<IHandlerRegistry>(new HandlerRegistry(handlerMappings));

            // Automatically register pipelines.
            var pipelineMappings = services.AddPipelineRegistryUsingGeneratedPipelines();
            services.AddSingleton<IPipelineRegistry>(new PipelineRegistry(pipelineMappings));

            Logger.LogInformation("CQRS service registration completed.");
            return services;
        }

        /// <summary>
        /// Builds and registers the metadata for requests and handlers using either the generated registry or reflection-based registration.
        /// </summary>
        private static ConcurrentDictionary<Type, RequestMetadata> RegisterAndBuildMetadata(
            IServiceCollection services,
            Assembly?[] assemblies)
        {
            Logger.LogInformation("Registering and building metadata for requests and handlers.");

            // Look for the analyzer-generated registry type.
            var generatedRegistryType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType($"{SourceGeneratorConstants.GeneratedNamespace}.{SourceGeneratorConstants.HandlerRegistryClassName}"))
                .FirstOrDefault(t => t != null);

            if (generatedRegistryType != null)
            {
                Logger.LogInformation("Found generated registry: {RegistryType}", generatedRegistryType.FullName);

                // Invoke its static registration method.
                var registerMethod = generatedRegistryType.GetMethod("RegisterHandlers", BindingFlags.Public | BindingFlags.Static);
                if (registerMethod == null)
                {
                    Logger.LogError("Generated registry found but its RegisterHandlers method is missing.");
                    throw new InvalidOperationException("Generated registry found but its RegisterHandlers method is missing.");
                }
                registerMethod.Invoke(null, new object[] { services });

                // First try to retrieve the generated MetadataMap property.
                var metadataProperty = generatedRegistryType.GetProperty(SourceGeneratorConstants.MetadataMapPropertyName, BindingFlags.Public | BindingFlags.Static);
                if (metadataProperty != null &&
                    metadataProperty.GetValue(null) is IReadOnlyDictionary<Type, RequestMetadata> generatedMetadata &&
                    generatedMetadata.Count > 0)
                {
                    Logger.LogInformation("Using generated MetadataMap from the AoT registry.");
                    return new ConcurrentDictionary<Type, RequestMetadata>(generatedMetadata);
                }

                // Otherwise, fallback to using the RegistrationMap property.
                var mappingProperty = generatedRegistryType.GetProperty(SourceGeneratorConstants.RegistrationMapPropertyName, BindingFlags.Public | BindingFlags.Static);
                if (mappingProperty == null)
                {
                    Logger.LogError("Registration mapping property not found in generated registry.");
                    throw new InvalidOperationException("Registration mapping property not found in generated registry.");
                }

                if (mappingProperty.GetValue(null) is not IReadOnlyDictionary<Type, (Type HandlerInterface, string RegistrationKind)> mapping)
                {
                    Logger.LogError("Registration mapping property on generated registry is null.");
                    throw new InvalidOperationException("Registration mapping property on generated registry is null.");
                }

                // Build a minimal RequestMetadata dictionary from the generated mapping.
                var dict = new ConcurrentDictionary<Type, RequestMetadata>();
                foreach (var (requestType, value) in mapping)
                {
                    var handlerInterface = value.HandlerInterface;
                    Logger.LogInformation("Mapping found: {RequestType} -> {HandlerInterface}", requestType.FullName, handlerInterface.FullName);
                    var metadata = BuildEmptyMetadata(requestType) with { HandlerType = handlerInterface };
                    dict[requestType] = metadata;
                }
                return dict;
            }

            // Fallback: Reflection-based registration.
            if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            {
                Logger.LogWarning("Generated registry not found in native AOT build. Reflection-based registration is not available.");
            }

            Logger.LogWarning("Generated registry not found; falling back to reflection-based registration.");
            return ReflectionBasedRegistration(services, assemblies);
        }

        /// <summary>
        /// Registers and categorizes request handlers, pipeline behaviors, and other relevant types
        /// from the specified assemblies into a metadata structure for further processing.
        /// </summary>
        private static ConcurrentDictionary<Type, RequestMetadata> ReflectionBasedRegistration(
            IServiceCollection services,
            Assembly?[] assemblies)
        {
            Logger.LogInformation("Starting reflection-based registration of request handlers and pipeline behaviors.");

            var requestMetadataMap = new ConcurrentDictionary<Type, RequestMetadata>();

            // Step 1: Get all types from the given assemblies.
            var allTypes = assemblies.SelectMany(a => a?.GetTypes() ?? Array.Empty<Type>())
                .Where(t => t.IsClass && !t.IsAbstract)
                .ToList();

            // Collect pipeline behaviors separately so they can be sorted by priority.
            var pipelineBehaviors = new List<(Type BehaviorType, int Priority)>();

            // Step 2: Categorize each type.
            foreach (var type in allTypes)
            {
                // (A) Handler registration.
                var handlerInterfaces = type.GetInterfaces().Where(i =>
                    i.IsGenericType &&
                    (i.GetGenericTypeDefinition() == typeof(ICommandHandler<>) ||
                     i.GetGenericTypeDefinition() == typeof(IQueryHandler<,>)));

                foreach (var handlerInterface in handlerInterfaces)
                {
                    services.AddTransient(handlerInterface, type);
                    // The request is the first generic argument.
                    var requestType = handlerInterface.GetGenericArguments()[0];

                    if (!requestMetadataMap.TryGetValue(requestType, out var existing))
                    {
                        existing = BuildEmptyMetadata(requestType);
                        requestMetadataMap[requestType] = existing;
                    }

                    var updated = existing with { HandlerType = handlerInterface };
                    requestMetadataMap[requestType] = updated;
                }

                // (B) Pipeline behavior discovery.
                var behaviorInterface = type.GetInterfaces().FirstOrDefault(i =>
                    i.IsGenericType &&
                    i.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>));
                if (behaviorInterface != null)
                {
                    var priorityAttr = type.GetCustomAttribute<PipelinePriorityAttribute>();
                    var priority = priorityAttr?.Priority ?? PipelinePriorityAttribute.DefaultPriority;
                    pipelineBehaviors.Add((type, priority));
                }

                // (C) Notification handlers.
                var notificationHandlerInterfaces = type.GetInterfaces().Where(i =>
                    i.IsGenericType &&
                    i.GetGenericTypeDefinition() == typeof(INotificationHandler<>));
                foreach (var notifHandlerInterface in notificationHandlerInterfaces)
                {
                    services.AddTransient(notifHandlerInterface, type);
                }

                // Process only types that implement IRequest.
                if (!typeof(IRequest).IsAssignableFrom(type))
                    continue;

                // Gather attribute data.
                var preHandlers = type.GetCustomAttributes(true)
                    .OfType<IPreHandlerAttribute>()
                    .OrderBy(a => a.PreHandlerExecutionPriority)
                    .ToArray();

                var postHandlers = type.GetCustomAttributes(true)
                    .OfType<IPostHandlerAttribute>()
                    .OrderBy(a => a.PostHandlerExecutionPriority)
                    .ToArray();

                var exemptionAttributes = type.GetCustomAttributes(typeof(PipelineExemptionAttribute), true)
                    .Cast<PipelineExemptionAttribute>()
                    .ToArray();

                // Gather sensitive data info.
                var sensitiveProps = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(p => p.GetCustomAttributes(typeof(SensitiveDataAttribute), false).Any())
                    .Select(p => new PropertySensitivity(p.Name, true))
                    .ToArray();

                // Determine if it’s a command or a query.
                var resultType = GetResultTypeForRequestType(type);

                // Build or update the metadata.
                if (!requestMetadataMap.TryGetValue(type, out var currentMeta))
                    currentMeta = BuildEmptyMetadata(type);

                var updatedMeta = currentMeta with
                {
                    PreHandlers = preHandlers,
                    PostHandlers = postHandlers,
                    PipelineExemptions = exemptionAttributes,
                    SensitiveProperties = sensitiveProps,
                    ResultType = resultType
                };
                requestMetadataMap[type] = updatedMeta;
            }

            // (E) Register pipeline behaviors in sorted order by priority.
            foreach (var (behaviorType, _) in pipelineBehaviors.OrderBy(pb => pb.Priority))
            {
                services.AddTransient(typeof(IPipelineBehavior<,>), behaviorType);
            }

            Logger.LogInformation("Reflection-based registration completed with {Count} request metadata entries.", requestMetadataMap.Count);
            return requestMetadataMap;
        }

        #region Helpers

        /// <summary>
        /// Creates and returns an empty instance of <see cref="RequestMetadata"/> for the given request type.
        /// </summary>
        private static RequestMetadata BuildEmptyMetadata(Type requestType)
        {
            return new RequestMetadata(
                requestType,
                null,
                Array.Empty<IPreHandlerAttribute>(),
                Array.Empty<IPostHandlerAttribute>(),
                Array.Empty<PipelineExemptionAttribute>(),
                Array.Empty<PropertySensitivity>(),
                null
            );
        }

        /// <summary>
        /// Determines the result type for a given request type.
        /// Checks if the request type implements IQuery{TResult} or ICommand, and returns the corresponding result type.
        /// </summary>
        private static Type GetResultTypeForRequestType(Type requestType)
        {
            var iQuery = requestType.GetInterfaces().FirstOrDefault(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQuery<>));

            if (iQuery != null)
            {
                var tResult = iQuery.GetGenericArguments()[0];
                return tResult;
            }

            if (typeof(ICommand).IsAssignableFrom(requestType))
                return typeof(CommandResult);

            throw new InvalidOperationException(
                $"Request type '{requestType.FullName}' implements IRequest but is neither ICommand nor IQuery<T>.");
        }

        #endregion
    }
}