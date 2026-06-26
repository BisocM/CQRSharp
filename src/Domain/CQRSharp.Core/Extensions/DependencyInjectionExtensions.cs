using System.Diagnostics.CodeAnalysis;
using CQRSharp.Abstractions.Interfaces.Idempotency;
using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Abstractions.Interfaces.Outbox;
using CQRSharp.Core.Background.Outbox;
using CQRSharp.Core.Background.Outbox.Types;
using CQRSharp.Core.Background.TaskQueue;
using CQRSharp.Core.Background.TaskQueue.Telemetry;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Exceptions;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Idempotency;
using CQRSharp.Core.Mediation;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Options;
using CQRSharp.Core.Options.Enums;
using CQRSharp.Core.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

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
	/// <param name="configureDispatcher">
	///     An optional action to configure dispatcher options such as <see cref="DispatcherOptions.RunMode" /> and
	///     <see cref="DispatcherOptions.ScopeMode" />.
	/// </param>
	/// <param name="configureValidation">
	///     An optional action to configure the fail-fast startup validator (its <see cref="CqrsValidationPolicy" />).
	///     Defaults to <see cref="CqrsValidationPolicy.ThrowOnError" />, aborting host start when a configuration error
	///     is found.
	/// </param>
	/// <returns>The <see cref="IServiceCollection" /> so that additional calls can be chained.</returns>
	public static IServiceCollection AddCqrs(this IServiceCollection services,
        Action<BackgroundTaskQueueOptions>? configureQueue = null,
        Action<OutboxOptions>? configureOutbox = null,
        Action<DispatcherOptions>? configureDispatcher = null,
        Action<CqrsStartupValidationOptions>? configureValidation = null)
    {
        services.AddOptions<BackgroundTaskQueueOptions>()
            .Configure(opts => configureQueue?.Invoke(opts))
            .Validate(o => o.Capacity > 0, "BackgroundTaskQueueOptions.Capacity must be greater than zero.")
            .Validate(o => o.CallbackChannelCapacity > 0, "BackgroundTaskQueueOptions.CallbackChannelCapacity must be greater than zero.")
            .Validate(o => o.NotificationMaxRetries >= 1, "BackgroundTaskQueueOptions.NotificationMaxRetries must be at least 1.")
            .ValidateOnStart();

        services.Configure<DispatcherOptions>(opts => configureDispatcher?.Invoke(opts));

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

        // The single clock seam: every time-dependent component reads "now" through TimeProvider, so behavior is
        // deterministic under test (via FakeTimeProvider) and overridable by consumers. Defaults to the system clock;
        // a consumer that registers their own TimeProvider before/after AddCqrs wins.
        services.TryAddSingleton(TimeProvider.System);

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

        // Fail-fast startup validation: surfaces silent fallbacks (an unbacked outbox, a transactional outbox that
        // cannot detect a transaction, outbox-bypassing notifications, a missing generated registry) as loud, early
        // failures at host start. TryAddEnumerable keeps the hosted service single even if AddCqrs runs twice.
        services.AddOptions<CqrsStartupValidationOptions>()
            .Configure(o => configureValidation?.Invoke(o))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, CqrsStartupValidator>());

        if (outboxProbe is not null)
            if (outboxProbe.Mode != OutboxMode.Disabled)
                services.AddOutboxProcessor();

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
        services.AddOptions<OutboxProcessorOptions>()
            .Configure(opts => configureOptions?.Invoke(opts))
            .Validate(o => o.PollingInterval > TimeSpan.Zero,
                "OutboxProcessorOptions.PollingInterval must be greater than zero (a non-positive interval would hot-loop the processor).")
            .Validate(o => o.BatchSize > 0, "OutboxProcessorOptions.BatchSize must be greater than zero.")
            .Validate(o => o.MaxRetryAttempts >= 1, "OutboxProcessorOptions.MaxRetryAttempts must be at least 1.")
            .ValidateOnStart();

        services.AddHostedService<OutboxProcessor>();
        return services;
    }

    /// <summary>
    ///     Registers the in-process in-memory outbox store as the <see cref="IOutboxStore" />. The store is NOT
    ///     durable — messages live in process memory and are lost on restart — so it is intended for development,
    ///     tests, and single-node demos, not production (use a database- or Redis-backed store there). The outbox
    ///     still needs the source-generated notification serializer and an outbox-enabled <c>AddCqrs</c> for the
    ///     processor to actually run.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">An optional action to configure the in-memory store options.</param>
    /// <returns>The service collection so that additional calls can be chained.</returns>
    public static IServiceCollection AddInMemoryOutboxStore(
        this IServiceCollection services,
        Action<InMemoryOutboxStoreOptions>? configure = null)
    {
        services.AddOptions<InMemoryOutboxStoreOptions>()
            .Configure(opts => configure?.Invoke(opts))
            .Validate(o => o.VisibilityTimeout > TimeSpan.Zero,
                "InMemoryOutboxStoreOptions.VisibilityTimeout must be greater than zero.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IOutboxStore, InMemoryOutboxStore>();

        return services;
    }

    /// <summary>
    ///     Registers the in-process in-memory idempotency store as the <see cref="IIdempotencyStore" />, so requests
    ///     implementing <c>IIdempotentRequest</c> are deduplicated. The store is NOT durable — claims live in process
    ///     memory and are lost on restart — so it deduplicates only within a single process lifetime; use a database-
    ///     or Redis-backed store for cross-process at-most-once semantics.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">An optional action to configure the in-memory idempotency store options.</param>
    /// <returns>The service collection so that additional calls can be chained.</returns>
    public static IServiceCollection AddInMemoryIdempotencyStore(
        this IServiceCollection services,
        Action<InMemoryIdempotencyStoreOptions>? configure = null)
    {
        services.AddOptions<InMemoryIdempotencyStoreOptions>()
            .Configure(opts => configure?.Invoke(opts))
            .Validate(o => o.Retention > TimeSpan.Zero,
                "InMemoryIdempotencyStoreOptions.Retention must be greater than zero.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();

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