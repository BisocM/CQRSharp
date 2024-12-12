using System.Collections.Concurrent;
using System.Reflection;
using CQRSharp.Core.BackgroundTasks;
using CQRSharp.Core.Dispatch;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Options;
using CQRSharp.Core.Pipelines;
using CQRSharp.Core.Pipelines.Attributes;
using CQRSharp.Core.Pipelines.Types;
using CQRSharp.Core.Pipelines.Types.RateLimiting;
using CQRSharp.Interfaces.Handlers;
using CQRSharp.Interfaces.Markers;
using CQRSharp.Interfaces.Markers.Command;
using CQRSharp.Interfaces.Markers.Query;
using CQRSharp.Interfaces.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Extensions;

/// <summary>
///     Provides extension methods for registering services related to the dispatcher.
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
        var handlerMappings = RegisterHandlersAndBehaviors(services, assemblies);
        services.AddSingleton<IHandlerRegistry>(new HandlerRegistry(handlerMappings));

        return services;
    }

    /// <summary>
    ///     Method for registration of the consumer-implemented user identification factory.
    ///     In order to implement the factory, create a new class that inherits from the
    ///     <see cref="IUserIdentificationFactory" />,
    ///     implement the interface and pass the class as a generic type here.
    /// </summary>
    /// <remarks>
    ///     Implemented as transient - does not keep instance data.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <typeparam name="TIdentifierFactory">
    ///     The user-implemented type which stores the unique method for generating
    ///     a user ID to be passed in each command's context.
    /// </typeparam>
    /// <returns>Returns IServiceCollection to allow chaining of registration calls.</returns>
    public static IServiceCollection AddUserIdentificationFactory<TIdentifierFactory>(this IServiceCollection services)
        where TIdentifierFactory : class, IUserIdentificationFactory
    {
        return services.AddTransient<IUserIdentificationFactory, TIdentifierFactory>();
    }

    /// <summary>
    ///     Method for registration of the consumer-implemented request identification factory.
    ///     In order to implement the factory, create a new class that inherits from
    ///     <see cref="IRequestIdentificationFactory" />,
    ///     implement the interface and pass the class as a generic type here.
    /// </summary>
    /// <remarks>
    ///     Implemented as a transient - does not keep instance data.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <typeparam name="TIdentifierFactory">
    ///     The user-implemented type which stores the unique method for generating
    ///     an ID for each command or query.
    /// </typeparam>
    /// <returns>
    ///     The updated <see cref="IServiceCollection" /> instance, enabling chaining of registration calls.
    /// </returns>
    public static IServiceCollection AddRequestIdentificationFactory<TIdentifierFactory>(
        this IServiceCollection services)
        where TIdentifierFactory : class, IRequestIdentificationFactory
    {
        return services.AddTransient<IRequestIdentificationFactory, TIdentifierFactory>();
    }

    /// <summary>
    ///     Adds the rate limiting behavior and all related services to the service collection.
    ///     This method registers the <see cref="RateLimitingBehavior{TRequest,TResult}" /> pipeline behavior, and a custom
    ///     user identifier factory class provided by the library consumer.
    /// </summary>
    /// <typeparam name="TIdentifierService">
    ///     The type that implements <see cref="IUserIdentificationFactory" /> used to retrieve unique user identifiers
    ///     for rate limiting purposes. This must be implemented and provided by the library consumer.
    /// </typeparam>
    /// <param name="services">
    ///     The <see cref="IServiceCollection" /> to which the rate limiting services will be added.
    /// </param>
    /// <param name="configureOptions">Configuration for the rate limiter.</param>
    /// <returns>
    ///     The updated <see cref="IServiceCollection" /> instance, enabling chaining of registration calls.
    /// </returns>
    /// <remarks>
    ///     This method adds essential components for rate limiting within the CQRS pipeline.
    ///     The implementation of <see cref="IUserIdentificationFactory" /> is added as transient - no instanced data.
    /// </remarks>
    public static IServiceCollection AddRateLimiting<TIdentifierService>(
        this IServiceCollection services,
        Action<RateLimiterOptions> configureOptions) where TIdentifierService : class, IUserIdentificationFactory
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

        //Register the user identifier factory that the user provides.
        services.AddTransient<IUserIdentificationFactory, TIdentifierService>();

        return services;
    }

    private static ConcurrentDictionary<Type, Type> RegisterHandlersAndBehaviors(IServiceCollection services,
        Assembly?[] assemblies)
    {
        var handlerMappings = new ConcurrentDictionary<Type, Type>();

        //Get all types from the specified assemblies.
        var allTypes = assemblies.SelectMany(a => a?.GetTypes() ?? Type.EmptyTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false });

        //Pipeline behaviors have to get separated so that we can adjust the priority in which they are executed, if needed.
        //Handling pipeline priority is done here for the reason that this is more performative than doing it at runtime.
        //At runtime, the Pipeline Builder in the dispatcher class would have to reflect on the pipeline object and find the priority attribute.
        var pipelineBehaviors = new List<(Type BehaviorType, int Priority)>();

        foreach (var type in allTypes)
        {
            //Register command handlers.
            var handlerInterfaces = type.GetInterfaces()
                .Where(i => i.IsGenericType &&
                            (i.GetGenericTypeDefinition() == typeof(ICommandHandler<>) ||
                             i.GetGenericTypeDefinition() == typeof(IQueryHandler<,>)));

            foreach (var handlerInterface in handlerInterfaces)
            {
                services.AddTransient(handlerInterface, type);

                //Map request type to handler interface.
                var requestType = handlerInterface.GetGenericArguments()[0];
                handlerMappings[requestType] = handlerInterface;
            }

            //Retrieve the behavior interface.
            var behaviorInterface = type.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType &&
                                     i.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>));

            if (behaviorInterface != null)
            {
                var priorityAttr = type.GetCustomAttribute<PipelinePriorityAttribute>();
                var priority = priorityAttr?.Priority ?? PipelinePriorityAttribute.DefaultPriority;
                pipelineBehaviors.Add((type, priority));
            }

            //Register notification handlers.
            var notificationHandlerInterfaces = type.GetInterfaces()
                .Where(i => i.IsGenericType &&
                            i.GetGenericTypeDefinition() == typeof(INotificationHandler<>));

            foreach (var notificationHandlerInterface in notificationHandlerInterfaces)
                services.AddTransient(notificationHandlerInterface, type);
        }

        //Sort behaviors by priority and register in sorted order.
        foreach (var (behaviorType, _) in pipelineBehaviors.OrderBy(pb => pb.Priority))
            services.AddTransient(typeof(IPipelineBehavior<,>), behaviorType);

        return handlerMappings;
    }
}