using CQRSharp.Core.BackgroundTasks;
using CQRSharp.Core.BackgroundTasks.Telemetry;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Options;
using CQRSharp.Core.Requests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Extensions;

/// <summary>
///     Provides extension methods for registering CQRSharp services with the dependency injection container.
/// </summary>
public static partial class DependencyInjectionExtensions
{
    /// <summary>
    ///     Registers the core services required for the CQRSharp library to function.
    ///     This is the primary entry point for setting up the library.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection" /> to add the services to.</param>
    /// <param name="configureDispatcher">An optional action to configure dispatcher options.</param>
    /// <param name="configureQueue">An optional action to configure the background task queue options.</param>
    /// <returns>The <see cref="IServiceCollection" /> so that additional calls can be chained.</returns>
    public static IServiceCollection AddCqrs(this IServiceCollection services,
        Action<DispatcherOptions>? configureDispatcher = null,
        Action<BackgroundTaskQueueOptions>? configureQueue = null)
    {
        services.Configure<DispatcherOptions>(opts => configureDispatcher?.Invoke(opts));
        services.Configure<BackgroundTaskQueueOptions>(opts => configureQueue?.Invoke(opts));

        services.AddSingleton<IQueueMetricsReporter, OpenTelemetryQueueMetricsReporter>();
        services.AddSingleton<IRequestDispatcher, RequestDispatcher>();
        services.AddSingleton<INotificationDispatcher, NotificationDispatcher>();
        services.AddTransient<IRequestContextFactory, DefaultRequestContextFactory>();

        services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
        services.AddHostedService<BackgroundTaskQueueConsumer>();

        services.AddLogging(lb =>
        {
            lb.AddConsole();
            lb.SetMinimumLevel(LogLevel.Information);
        });

        return services;
    }
}