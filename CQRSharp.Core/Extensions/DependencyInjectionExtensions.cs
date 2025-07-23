using CQRSharp.Abstractions.Data.Interfaces.Outbox;
using CQRSharp.Core.Background.Outbox;
using CQRSharp.Core.Background.TaskQueue;
using CQRSharp.Core.Background.TaskQueue.Telemetry;
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
public static class DependencyInjectionExtensions
{
    /// <summary>
    ///     Registers the core services required for the CQRSharp library to function.
    ///     This is the primary entry point for setting up the library.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection" /> to add the services to.</param>
    /// <param name="configureDispatcher">An optional action to configure dispatcher options.</param>
    /// <param name="configureQueue">An optional action to configure the background task queue options.</param>
    /// <param name="configureOutbox">An optional action to configure notification outbox options.</param>
    /// <returns>The <see cref="IServiceCollection" /> so that additional calls can be chained.</returns>
    public static IServiceCollection AddCqrs(this IServiceCollection services,
        Action<DispatcherOptions>? configureDispatcher = null,
        Action<BackgroundTaskQueueOptions>? configureQueue = null,
        Action<OutboxOptions>? configureOutbox = null)
    {
        services.Configure<DispatcherOptions>(opts => configureDispatcher?.Invoke(opts));
        services.Configure<BackgroundTaskQueueOptions>(opts => configureQueue?.Invoke(opts));
        services.Configure<OutboxOptions>(opts => configureOutbox?.Invoke(opts));

        services.AddSingleton<IQueueMetricsReporter, OpenTelemetryQueueMetricsReporter>();
        services.AddSingleton<IRequestDispatcher, RequestDispatcher>();
        services.AddTransient<IRequestContextFactory, DefaultRequestContextFactory>();

        // Register the main dispatcher as scoped. It decides whether to dispatch directly or use the outbox.
        services.AddSingleton<IDirectNotificationDispatcher, DirectNotificationDispatcher>();
        services.AddScoped<INotificationDispatcher, NotificationDispatcher>();

        services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
        services.AddHostedService<BackgroundTaskQueueConsumer>();

        services.AddLogging(lb =>
        {
            lb.AddConsole();
            lb.SetMinimumLevel(LogLevel.Information);
        });

        return services;
    }

    /// <summary>
    ///     Registers the outbox processor as a hosted background service.
    ///     This is required for notifications sent via the outbox pattern to be processed.
    ///     It is recommended to also register an <see cref="IOutboxStore" /> implementation.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">An action to configure the outbox processor options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddOutboxProcessor(
        this IServiceCollection services,
        Action<OutboxProcessorOptions>? configureOptions = null)
    {
        services.Configure<OutboxProcessorOptions>(opts => { configureOptions?.Invoke(opts); });

        services.AddHostedService<OutboxProcessor>();
        return services;
    }
}