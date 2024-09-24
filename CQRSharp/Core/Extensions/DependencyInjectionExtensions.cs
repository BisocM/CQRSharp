using CQRSharp.Core.Dispatch;
using CQRSharp.Core.Pipeline;
using CQRSharp.Interfaces.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;

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
        /// <param name="assemblies">Assemblies to scan for handlers and attributes.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddDispatcher(this IServiceCollection services, params Assembly[] assemblies)
        {
            //Register the dispatcher as a singleton service.
            services.AddSingleton<IDispatcher, Dispatcher>();

            //Configure logging.
            services.AddLogging(loggingBuilder =>
            {
                loggingBuilder.AddConsole();
                loggingBuilder.SetMinimumLevel(LogLevel.Information);
            });

            //Define the handler and pipeline behavior interfaces to search for.
            var handlerInterfaces = new[]
            {
                typeof(ICommandHandler<>),
                typeof(IQueryHandler<,>)
            };

            var pipelineBehaviorInterfaceType = typeof(IPipelineBehavior<,>);

            //Retrieve all relevant types from the specified assemblies.
            var types = assemblies.SelectMany(a =>
            {
                try
                {
                    return a.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    //Return types that were successfully loaded.
                    return ex.Types.Where(t => t != null);
                }
            })
            .Where(t => t != null && !t.IsAbstract && !t.IsInterface);

            foreach (var type in types)
            {
                //Get all implemented interfaces of the type.
                var interfaces = type!.GetInterfaces()
                                     .Where(i => i != null && i.IsGenericType);

                foreach (var @interface in interfaces)
                {
                    var genericTypeDefinition = @interface.GetGenericTypeDefinition();

                    if (handlerInterfaces.Contains(genericTypeDefinition))
                    {
                        //Register command handler interfaces.
                        services.AddTransient(@interface, type);
                    }
                    else if (genericTypeDefinition == pipelineBehaviorInterfaceType)
                    {
                        //Register pipeline behaviors as open generic types.
                        services.AddTransient(typeof(IPipelineBehavior<,>), type.GetGenericTypeDefinition());
                    }
                }
            }

            return services;
        }

        /// <summary>
        /// Adds pipeline behaviors to the service collection.
        /// </summary>
        /// <param name="services">The service collection to add pipeline behaviors to.</param>
        /// <param name="pipelineTypes">An array of pipeline behavior types to register.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddPipelines(this IServiceCollection services, params Type[] pipelineTypes)
        {
            foreach (var pipelineType in pipelineTypes)
            {
                //Validate that the type implements IPipelineBehavior<TRequest, TResponse>
                var interfaces = pipelineType.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>));

                if (!interfaces.Any())
                    throw new ArgumentException($"Type {pipelineType.Name} does not implement IPipelineBehavior<TRequest, TResponse>");

                //Register the pipeline behavior as an open generic type
                services.AddTransient(typeof(IPipelineBehavior<,>), pipelineType);
            }

            return services;
        }
    }
}