using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CQRSharp.Core.BackgroundTasks;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Options;
using CQRSharp.Core.Requests;
using CQRSharp.Shared.Core.Data.Interfaces.Markers.Query;
using CQRSharp.Shared.Core.Data.Models.Commands;
using CQRSharp.Shared.Core.Data.Models.Requests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Extensions;

/// <summary>
///     Provides extension methods for configuring and integrating CQRS functionality
///     with Microsoft.Extensions.DependencyInjection.
/// </summary>
public static partial class DependencyInjectionExtensions
{
    /// <summary>
    ///     Adds the dispatcher and command handlers to the service collection.
    ///     Responsible for automatic registration of all ICommand, IQuery{TResult},
    ///     INotification, and IPipelineBehavior{TRequest, TResult} implementations.
    /// </summary>
    public static IServiceCollection AddCqrs(this IServiceCollection services,
        Action<DispatcherOptions>? configureOptions)
    {
        Logger.LogInformation("Starting CQRS service registration.");

        //Create and register DispatcherOptions.
        var options = new DispatcherOptions();
        configureOptions?.Invoke(options);
        services.AddSingleton(options);

        //Register default components.
        services.AddTransient<IRequestContextFactory, DefaultRequestContextFactory>();
        services.AddSingleton<IDispatcher, Dispatcher>();
        services.AddSingleton<NotificationDispatcher>();
        services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
        services.AddHostedService<BackgroundTaskService>();

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

        Logger.LogInformation("CQRS service registration completed.");
        return services;
    }
}