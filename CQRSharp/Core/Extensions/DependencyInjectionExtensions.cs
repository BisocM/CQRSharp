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

                    //Map request type to handler type
                    var requestType = handlerInterface.GetGenericArguments()[0];
                    handlerMappings[requestType] = handlerInterface;
                }

                //Register pipeline behaviors.
                var behaviorInterfaces = type.GetInterfaces()
                    .Where(i => i.IsGenericType &&
                                i.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>));
                
                if (behaviorInterfaces.Any())
                    services.AddTransient(typeof(IPipelineBehavior<,>), type);

                //Register notification handlers.
                var notificationHandlerInterfaces = type.GetInterfaces()
                    .Where(i => i.IsGenericType &&
                                i.GetGenericTypeDefinition() == typeof(INotificationHandler<>));

                foreach (var notificationHandlerInterface in notificationHandlerInterfaces)
                    services.AddTransient(notificationHandlerInterface, type);
            }

            return handlerMappings;
        }
    }
}