using System.Collections.Concurrent;
using System.Reflection;
using CQRSharp.Core.BackgroundTasks;
using CQRSharp.Core.Caching;
using CQRSharp.Core.Dispatch;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Options;
using CQRSharp.Core.Pipelines;
using CQRSharp.Core.Pipelines.Attributes;
using CQRSharp.Core.Pipelines.Attributes.Markers;
using CQRSharp.Core.Pipelines.Types;
using CQRSharp.Core.Pipelines.Types.RateLimiting;
using CQRSharp.Data.Commands;
using CQRSharp.Interfaces.Handlers;
using CQRSharp.Interfaces.Markers.Command;
using CQRSharp.Interfaces.Markers.Query;
using CQRSharp.Interfaces.Markers.Request;
using CQRSharp.Interfaces.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Extensions;

/// <summary>
///     Provides extension methods for configuring dependency injection for CQRS-related services.
/// </summary>
public static class DependencyInjectionExtensions
{
    /// <summary>
    ///     Adds the dispatcher and command handlers to the service collection.
    ///     <para>
    ///         Responsible for automatic registration of all <see cref="ICommand" />, <see cref="IQuery{TResult}" />,
    ///         <see cref="INotification" />, and <see cref="IPipelineBehavior{TRequest,TResult}" /> implementations in the
    ///         specified assemblies.
    ///         Automatically includes the library assembly in the assemblies to scan, meaning that native pipelines -
    ///         <see cref="ExecutionLoggingBehavior{TRequest,TResult}" />,
    ///         <see cref="ResilienceBehavior{TRequest,TResult}" />, <see cref="TimeoutBehavior{TRequest,TResult}" /> - are
    ///         automatically activated.
    ///     </para>
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configureOptions">Configure the options for the dispatcher to use.</param>
    /// <param name="assemblies">Assemblies to scan for handlers and attributes.</param>
    /// <returns>The updated service collection.</returns>
    public static IServiceCollection AddCqrs(this IServiceCollection services,
        Action<DispatcherOptions>? configureOptions, params Assembly?[] assemblies)
    {
        //Append the library's assembly to the assemblies array so that the pipelines pre-defined in the library are also registered by the container.
        assemblies = assemblies.Append(Assembly.GetAssembly(typeof(DependencyInjectionExtensions))).ToArray();

        //Create a new instance of DispatcherOptions.
        var options = new DispatcherOptions();
        configureOptions?.Invoke(options);

        //Register the options as a singleton service.
        services.AddSingleton(options);

        //Add the default context factory. If the user wants their own, they will just have to override this with their own transient call.
        services.AddTransient<IRequestContextFactory, DefaultRequestContextFactory>();

        //Register the dispatcher as a singleton service.
        services.AddSingleton<IDispatcher, Dispatcher>();

        //Register the event manager as a singleton service.
        services.AddSingleton<NotificationDispatcher>();

        //Register the background task queue and service.
        services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
        services.AddHostedService<BackgroundTaskService>();

        //Configure logging.
        services.AddLogging(loggingBuilder =>
        {
            loggingBuilder.AddConsole();
            loggingBuilder.SetMinimumLevel(LogLevel.Information);
        });

        //Automatically register handlers and pipeline behaviors.
        //Register the handler registry as a singleton service.
        var handlerMappings = RegisterAndBuildMetadata(services, assemblies);
        services.AddSingleton<IHandlerRegistry>(new HandlerRegistry(handlerMappings));

        return services;
    }

    /// <summary>
    ///     Adds the rate limiting behavior and all related services to the service collection.
    ///     This method registers the <see cref="RateLimitingBehavior{TRequest,TResult}" /> pipeline behavior, and a custom
    ///     user identifier factory class provided by the library consumer.
    /// </summary>
    /// <param name="services">
    ///     The <see cref="IServiceCollection" /> to which the rate limiting services will be added.
    /// </param>
    /// <param name="configureOptions">Configuration for the rate limiter.</param>
    /// <returns>
    ///     The updated <see cref="IServiceCollection" /> instance, enabling chaining of registration calls.
    /// </returns>
    public static IServiceCollection AddRateLimiting(
        this IServiceCollection services,
        Action<RateLimiterOptions> configureOptions)
    {
        if (configureOptions == null)
            throw new ArgumentNullException(nameof(configureOptions), "Rate limiting configuration must be provided.");

        var config = new RateLimiterOptions
        {
            MaxTokens = 0,
            ReplenishRatePerSecond = 0,
            Scope = RateLimitScope.Global
        };
        configureOptions(config);

        if (config.MaxTokens <= 0 || config.ReplenishRatePerSecond <= 0)
            throw new ArgumentException(
                "Rate limiting configuration is invalid. MaxTokens and ReplenishRatePerSecond must be greater than zero.");

        services.AddSingleton(config);
        services.AddSingleton<RateLimiter>();

        return services;
    }

    private static ConcurrentDictionary<Type, RequestMetadata> RegisterAndBuildMetadata(
        IServiceCollection services,
        Assembly?[] assemblies)
    {
        var requestMetadataMap = new ConcurrentDictionary<Type, RequestMetadata>();

        //Step 1: get all types from given assemblies
        var allTypes = assemblies.SelectMany(a => a?.GetTypes() ?? Type.EmptyTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .ToList();

        //We'll collect pipeline behaviors separately so we can sort them
        var pipelineBehaviors = new List<(Type BehaviorType, int Priority)>();

        //Step 2: We iterate once, categorizing each type
        foreach (var type in allTypes)
        {
            //(A) Handler registration
            var handlerInterfaces = type.GetInterfaces().Where(i =>
                i.IsGenericType &&
                (i.GetGenericTypeDefinition() == typeof(ICommandHandler<>) ||
                 i.GetGenericTypeDefinition() == typeof(IQueryHandler<,>)));

            foreach (var handlerInterface in handlerInterfaces)
            {
                services.AddTransient(handlerInterface, type);

                //Also store a mapping from the "request type" to the "handler interface"
                //so we can fill in our metadata. The request is the 1st generic arg.
                var requestType = handlerInterface.GetGenericArguments()[0];
                //We'll see if we already have an entry in requestMetadataMap
                if (!requestMetadataMap.TryGetValue(requestType, out var existing))
                {
                    existing = BuildEmptyMetadata(requestType);
                    requestMetadataMap[requestType] = existing;
                }

                // Create a new RequestMetadata with an updated HandlerType
                var updated = existing with { HandlerType = handlerInterface };
                requestMetadataMap[requestType] = updated;
            }

            //(B) Pipeline behavior discovery
            var behaviorInterface = type.GetInterfaces().FirstOrDefault(i =>
                i.IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>));
            if (behaviorInterface != null)
            {
                var priorityAttr = type.GetCustomAttribute<PipelinePriorityAttribute>();
                var priority = priorityAttr?.Priority ?? PipelinePriorityAttribute.DefaultPriority;
                pipelineBehaviors.Add((type, priority));
            }

            //(C) Notification handlers
            var notificationHandlerInterfaces = type.GetInterfaces().Where(i =>
                i.IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(INotificationHandler<>));
            foreach (var notifHandlerInterface in notificationHandlerInterfaces)
                services.AddTransient(notifHandlerInterface, type);

            //(D) If the type is an IRequest
            if (!typeof(IRequest).IsAssignableFrom(type)) continue;

            //Gather the attribute data
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

            //Gather sensitive data info
            var sensitiveProps = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.GetCustomAttributes(typeof(SensitiveDataAttribute), false).Any())
                .Select(p => new PropertySensitivity(p.Name, true))
                .ToArray();

            //Figure out if it’s a command or a query
            var resultType = GetResultTypeForRequestType(type);

            //Build or update the metadata
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

        //(E) Register pipeline behaviors in sorted order by priority
        foreach (var (behaviorType, _) in pipelineBehaviors.OrderBy(pb => pb.Priority))
            services.AddTransient(typeof(IPipelineBehavior<,>), behaviorType);

        return requestMetadataMap;
    }

    /// <summary>
    ///     Creates and returns an empty instance of <see cref="RequestMetadata" /> for the given request type.
    ///     Helper method to produce an "empty" metadata record if we haven't built it yet
    /// </summary>
    /// <param name="requestType">The type of the request for which metadata is being built.</param>
    /// <returns>An instance of <see cref="RequestMetadata" /> with default values.</returns>
    private static RequestMetadata BuildEmptyMetadata(Type requestType)
    {
        return new RequestMetadata(
            requestType,
            null,
            [],
            [],
            null,
            [],
            null
        );
    }


    /// <summary>
    ///     Determines if a given request type is a command or a query and extracts the result type, if applicable.
    ///     <para>
    ///         Checks if the request type implements <see cref="IQuery{TResult}" /> to identify it as a query and extracts
    ///         the generic result type. Otherwise, checks if the type implements <see cref="ICommand" /> to identify it as a
    ///         command and assigns a predefined result of type <see cref="CommandResult" />.
    ///     </para>
    /// </summary>
    /// <param name="requestType">
    ///     The type of the request to evaluate. This should implement either <see cref="IQuery{TResult}" /> or
    ///     <see cref="ICommand" />, or neither to evaluate as unsupported.
    /// </param>
    /// <returns>
    ///     A tuple containing the evaluation result:
    ///     - A boolean indicating whether the request is a command (true) or a query (false).
    ///     - The result type of the request, or null if the type is unsupported or does not have a result.
    /// </returns>
    private static Type GetResultTypeForRequestType(Type requestType)
    {
        var iQuery = requestType.GetInterfaces().FirstOrDefault(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQuery<>));

        if (iQuery != null)
        {
            //e.g. IQuery<TResult>
            var tResult = iQuery.GetGenericArguments()[0];
            return tResult;
        }

        if (typeof(ICommand).IsAssignableFrom(requestType))
            return typeof(CommandResult);

        throw new InvalidOperationException(
            $"Request type '{requestType.FullName}' implements IRequest but is neither ICommand nor IQuery<T>.");
    }
}