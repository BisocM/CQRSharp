using System.Collections.Concurrent;
using CQRSharp.Core.Dispatch;
using CQRSharp.Core.Options;
using CQRSharp.Core.Pipeline;
using CQRSharp.Interfaces.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;
using CQRSharp.Core.BackgroundTasks;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Pipeline.Attributes;
using CQRSharp.Core.Pipeline.Types;
using CQRSharp.Interfaces.Markers;
using CQRSharp.Interfaces.Markers.Command;
using CQRSharp.Interfaces.Markers.Query;
using CQRSharp.Interfaces.Notifications;

namespace CQRSharp.Core.Extensions
{
    /// <summary>
    /// Provides extension methods for registering services related to the dispatcher.
    /// </summary>
    public static class DependencyInjectionExtensions
    {
        /// <summary>
        /// Adds the dispatcher and command handlers to the service collection.
        /// <para>
        /// Responsible for automatic registration of all <see cref="ICommand"/>, <see cref="IQuery{TResult}"/>, <see cref="INotification"/>, and <see cref="IPipelineBehavior{TRequest,TResult}"/> implementations in the specified assemblies.
        /// Automatically includes the library assembly in the assemblies to scan, meaning that native pipelines - <see cref="ExecutionLoggingBehavior{TRequest,TResult}"/>,
        /// <see cref="ResilienceBehavior{TRequest,TResult}"/>, <see cref="TimeoutBehavior{TRequest,TResult}"/> - are automatically activated.
        /// </para>
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

        private static ConcurrentDictionary<Type, Type> RegisterHandlersAndBehaviors(IServiceCollection services, Assembly?[] assemblies)
        {
            var handlerMappings = new ConcurrentDictionary<Type, Type>();

            //Get all types from the specified assemblies.
            var allTypes = assemblies.SelectMany(a => a?.GetTypes() ?? Type.EmptyTypes).Where(t => t is { IsClass: true, IsAbstract: false });

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
}