using CQRSharp.Core.BackgroundTasks;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Options;
using CQRSharp.Core.Requests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Extensions;

/// <summary>
///     Provides extension methods for configuring and integrating CQRS functionality
///     with Microsoft.Extensions.DependencyInjection.
/// </summary>
public static partial class DependencyInjectionExtensions
{
    //A static logger for configuration-time logging.
    private static readonly ILogger Logger = LoggerFactory.Create(builder =>
    {
        builder.AddConsole();
        builder.SetMinimumLevel(LogLevel.Information);
    }).CreateLogger("DependencyInjectionExtensions");
    
    /// <summary>
    ///     Adds the dispatcher and command handlers to the service collection.
    ///     Responsible for automatic registration of all ICommand, IQuery{TResult},
    ///     INotification, and IPipelineBehavior{TRequest, TResult} implementations.
    /// </summary>
    public static IServiceCollection AddCqrs(this IServiceCollection services,
        Action<DispatcherOptions>? configureOptions)
    {
        Logger.LogInformation("Starting CQRS service registration.");
        services.Configure<DispatcherOptions>(options =>
        {
            //Apply the delegate if it is provided.
            configureOptions?.Invoke(options);
        });

        //Register default components.
        services.AddSingleton<IDispatcher, Dispatcher>();
        services.AddSingleton<INotificationDispatcher, NotificationDispatcher>();
        services.AddTransient<IRequestContextFactory, DefaultRequestContextFactory>();
        services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();

        //Configure logging.
        services.AddLogging(loggingBuilder =>
        {
            loggingBuilder.AddConsole();
            loggingBuilder.SetMinimumLevel(LogLevel.Information);
        });

        //Register handlers from the generated file.
        var serviceCount = services.Count;
        AddGeneratedHandlers(services);
        var registeredHandlers = services.Count - serviceCount;
        Logger.LogInformation($"Successfully registered {registeredHandlers} request handlers.");

        //Register the pipeline registry
        AddGeneratedPipelineRegistry(services);
        Logger.LogInformation("Successfully registered the pipeline registry.");

        //Register the request data registry
        AddGeneratedRequestRegistry(services);
        Logger.LogInformation("Successfully registered the request data registry.");

        //Register the handler registry
        AddGeneratedHandlerRegistry(services);
        Logger.LogInformation("Successfully registered the handler registry.");

        AddGeneratedFactories(services);
        Logger.LogInformation("Successfully registered the context factory registry.");
        
        Logger.LogInformation("CQRS service registration completed.");
        return services;
    }
}