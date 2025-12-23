using System.Diagnostics.CodeAnalysis;
using CQRSharp.Abstractions.Data.Interfaces.Notifications;
using CQRSharp.Abstractions.Data.Interfaces.Outbox;
using CQRSharp.Core.Background.Outbox;
using CQRSharp.Core.Background.Outbox.Types;
using CQRSharp.Core.Background.TaskQueue;
using CQRSharp.Core.Background.TaskQueue.Telemetry;
using CQRSharp.Core.Exceptions;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Mediation;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Options;
using CQRSharp.Core.Options.Enums;
using CQRSharp.Core.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    /// <param name="configureQueue">An optional action to configure the background task queue options.</param>
    /// <param name="configureOutbox">An optional action to configure notification outbox options.</param>
    /// <returns>The <see cref="IServiceCollection" /> so that additional calls can be chained.</returns>
	    public static IServiceCollection AddCqrs(this IServiceCollection services,
	        Action<BackgroundTaskQueueOptions>? configureQueue = null,
	        Action<OutboxOptions>? configureOutbox = null)
	    {
	        services.Configure<BackgroundTaskQueueOptions>(opts => configureQueue?.Invoke(opts));

	        OutboxOptions? outboxProbe = null;
	        if (configureOutbox is not null)
	        {
	            outboxProbe = new OutboxOptions();
	            configureOutbox(outboxProbe);

	            var outboxMode = outboxProbe.Mode;
	            services.Configure<OutboxOptions>(opts => { opts.Mode = outboxMode; });
	        }
	        else
	        {
	            services.Configure<OutboxOptions>(_ => { });
	        }

        services.TryAddScoped<IOutbox, Outbox>();

        services.TryAddSingleton<IQueueMetricsReporter, OpenTelemetryQueueMetricsReporter>();
        services.TryAddTransient<IRequestContextFactory, DefaultRequestContextFactory>();

        services.TryAddScoped<IPipelineExecutor, PipelineExecutor>();
        services.TryAddSingleton<IRequestExceptionHookRegistry, RequestExceptionHookRegistry>();

        services.TryAddScoped<IDirectNotificationDispatcher, DirectNotificationDispatcher>();
        services.TryAddScoped<INotificationDispatcher, NotificationDispatcher>();

        // Single CQRSharp façade: inject one thing (scoped to preserve DI scope semantics).
        services.TryAddScoped<ICqrsDispatcher, CqrsDispatcher>();

        services.TryAddSingleton<BackgroundTaskQueue>();
        services.TryAddSingleton<IBackgroundTaskQueue>(sp => sp.GetRequiredService<BackgroundTaskQueue>());
        services.TryAddSingleton<IBackgroundTaskManager>(sp => sp.GetRequiredService<BackgroundTaskQueue>());
        services.AddHostedService<BackgroundTaskQueueConsumer>();

        services.AddLogging();

	        if (outboxProbe is not null)
	        {
	            if (outboxProbe.Mode != OutboxMode.Disabled)
	                services.AddOutboxProcessor();
	        }

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

    /// <summary>
    ///     Registers a custom implementation of INotificationSerializer as a singleton.
    ///     This method is compatible with trimming and Native AOT.
    /// </summary>
    /// <param name="services">The IServiceCollection to add the service to.</param>
    /// <typeparam name="TSerializer">The type of the concrete serializer implementation.</typeparam>
    /// <returns>The IServiceCollection so that additional calls can be chained.</returns>
    public static IServiceCollection AddNotificationSerializer<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSerializer>(
        this IServiceCollection services)
        where TSerializer : class, INotificationSerializer
    {
        services.AddSingleton<INotificationSerializer, TSerializer>();
        return services;
    }
}
