using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using CQRSharp.Core;
using CQRSharp.Core.BackgroundTasks;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Exceptions;
using CQRSharp.Core.Idempotency;
using CQRSharp.Core.Modules;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Outbox;
using CQRSharp.Core.Pipelines;
using CQRSharp.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CQRSharp;

/// <summary>
///     Provides extension methods for registering CQRSharp services with the dependency injection container.
/// </summary>
public static class DependencyInjectionExtensions
{
    /// <summary>
    ///     The core registration the source-generated <c>AddCqrsGenerated</c> entry points call. Call
    ///     <c>AddCqrsGenerated(...)</c> instead: this registers the dispatcher but not the source-generated handler routing,
    ///     so on its own the first <c>Send</c>/<c>Stream</c>/<c>Publish</c> finds no handler. Options are configured through
    ///     the <c>AddCqrsGenerated</c> builder or <c>services.Configure&lt;T&gt;</c>.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection" /> to add the services to.</param>
    /// <returns>The <see cref="IServiceCollection" /> so that additional calls can be chained.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IServiceCollection AddCqrs(this IServiceCollection services)
    {
        // Every options type is validated at host start by a validator class registered once, so a second AddCqrs (each
        // AddCqrsGenerated call runs one) reports a failure once rather than once per call.
        AddValidatedOptions<BackgroundTaskQueueOptions, BackgroundTaskQueueOptionsValidator>(services);
        AddValidatedOptions<DispatcherOptions, DispatcherOptionsValidator>(services);
        AddValidatedOptions<OutboxOptions, OutboxOptionsValidator>(services);

        // The single clock seam: every time-dependent component reads "now" through TimeProvider, so behavior is
        // deterministic under test (via FakeTimeProvider) and overridable by consumers. Defaults to the system clock;
        // a consumer that registers their own TimeProvider before/after AddCqrs wins.
        services.TryAddSingleton(TimeProvider.System);

        // The scope's outbox buffer is a runtime detail with ownership rules only the runtime keeps: resolved as itself.
        services.TryAddScoped<ScopedOutbox>();

        // The wake-up between whoever stores outbox messages in this process and the processor: cheap, so always there.
        services.TryAddSingleton<OutboxSignal>();
        services.TryAddSingleton<IOutboxSignal>(sp => sp.GetRequiredService<OutboxSignal>());

        // Per-scope objects are built from factories over pre-resolved singletons: creating a DI scope and dispatching
        // once (every web request) should cost a few allocations, not a series of container lookups.
        services.TryAddSingleton<RequestPlanCache>();
        services.TryAddSingleton<CqrsMetrics>();
        services.TryAddSingleton<PipelineExecutorShared>();
        services.TryAddSingleton<IRequestExceptionHookRegistry>(RequestExceptionHookRegistry.Empty);

        AddValidatedOptions<NotificationOptions, NotificationOptionsValidator>(services);

        // What an in-process publish needs that is the same for every scope, built once from the modules the composition
        // registers; the scoped dispatcher only adds the scope.
        services.TryAddSingleton(sp => new NotificationRouting(
            sp.GetServices<ICqrsModule>(),
            sp.GetService<INotificationSubscriptionRegistry>(),
            sp.GetRequiredService<IOptions<NotificationOptions>>().Value.PublishStrategy,
            sp.GetService<IServiceProviderIsService>(),
            sp.GetRequiredService<CqrsMetrics>()));
        services.TryAddScoped<IDirectNotificationDispatcher>(sp => new DirectNotificationDispatcher(sp, sp.GetRequiredService<NotificationRouting>()));
        services.TryAddScoped<INotificationDispatcher, NotificationDispatcher>();

        // Single CQRSharp façade: inject one thing (scoped to preserve DI scope semantics).
        services.TryAddScoped<ICqrsDispatcher>(sp => new CqrsDispatcher(sp));

        // Readiness signal so a RunMode.Queued dispatch can detect a started consumer (and fail loudly, not hang,
        // when the host never starts it).
        services.TryAddSingleton<ConsumerReadiness>();

        services.TryAddSingleton<BackgroundTaskQueue>();
        services.TryAddSingleton<IBackgroundTaskQueue>(sp => sp.GetRequiredService<BackgroundTaskQueue>());
        services.TryAddSingleton<IBackgroundTaskManager>(sp => sp.GetRequiredService<BackgroundTaskQueue>());
        services.AddHostedService<BackgroundTaskQueueConsumer>();

        services.AddLogging();

        // Fail-fast startup validation: surfaces silent fallbacks (an unbacked outbox, a transactional outbox with no
        // unit of work, outbox-bypassing notifications, a marker without its behavior) as loud, early failures before any
        // hosted service starts. TryAddEnumerable keeps the hosted service single even if AddCqrs runs twice.
        AddValidatedOptions<CqrsStartupValidationOptions, CqrsStartupValidationOptionsValidator>(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, CqrsStartupValidator>());

        // The outbox processor is always registered: whether the outbox is on is only known once the options are
        // final (a Configure<OutboxOptions> or a configuration binding after this call counts too), so the processor
        // reads the effective mode when the host starts and idles while it is Disabled.
        services.AddOutboxProcessor();

        return services;
    }

    // The processor's options (validated on start) and the hosted service itself. Tuned through
    // UseOutbox(o => o.ConfigureProcessor(...)) or services.Configure<OutboxProcessorOptions>(...).
    internal static IServiceCollection AddOutboxProcessor(this IServiceCollection services)
    {
        AddValidatedOptions<OutboxProcessorOptions, OutboxProcessorOptionsValidator>(services);

        services.AddHostedService<OutboxProcessor>();
        return services;
    }

    private static void AddValidatedOptions<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TValidator>(IServiceCollection services)
        where TOptions : class
        where TValidator : class, IValidateOptions<TOptions>
    {
        services.AddOptions<TOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TOptions>, TValidator>());
    }

    /// <summary>
    ///     Registers the in-process in-memory outbox store as the <see cref="IOutboxStore" />, together with the matching
    ///     in-memory <see cref="IInboxStore" />. The store is NOT durable — messages live in process memory and are lost
    ///     on restart — so it is intended for development, tests, and single-node demos, not production (use a database-
    ///     or Redis-backed store there). Like every explicit store registration it <b>replaces</b> the outbox and inbox
    ///     stores already registered, whichever registered them, so the last explicit choice wins. The store is used once
    ///     the outbox is on (the builder's <c>UseOutbox(...)</c>, or <see cref="OutboxOptions.Mode" />); prefer
    ///     <c>UseOutbox(o =&gt; o.UseInMemoryStore())</c>, which turns it on and registers this store in one step.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">An optional action to configure the in-memory store options.</param>
    /// <returns>The service collection so that additional calls can be chained.</returns>
    public static IServiceCollection AddInMemoryOutboxStore(
        this IServiceCollection services,
        Action<InMemoryOutboxStoreOptions>? configure = null)
    {
        AddInMemoryOutboxStoreOptions(services, configure);

        // Swapped as a pair: a durable outbox left beside this inbox (or the other way round) would record deliveries
        // somewhere other than where the messages live.
        services.RemoveAll<IOutboxStore>();
        services.RemoveAll<IInboxStore>();
        services.AddSingleton<IOutboxStore, InMemoryOutboxStore>();
        services.AddSingleton<IInboxStore, InMemoryInboxStore>();

        return services;
    }

    // The builder's default for a bare UseOutbox(...): the in-memory pair, only while no outbox store is registered at
    // all. A store registered earlier is kept, one registered later replaces this, and a custom outbox store without
    // an inbox is not given an in-memory one.
    internal static void AddInMemoryOutboxStoreFallback(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(IOutboxStore))) return;

        AddInMemoryOutboxStoreOptions(services, null);
        services.AddSingleton<IOutboxStore, InMemoryOutboxStore>();
        services.TryAddSingleton<IInboxStore, InMemoryInboxStore>();
    }

    private static void AddInMemoryOutboxStoreOptions(IServiceCollection services, Action<InMemoryOutboxStoreOptions>? configure)
    {
        AddValidatedOptions<InMemoryOutboxStoreOptions, InMemoryOutboxStoreOptionsValidator>(services);
        if (configure is not null) services.Configure(configure);

        services.TryAddSingleton(TimeProvider.System);
    }

    /// <summary>
    ///     Registers the in-process in-memory idempotency store as the <see cref="IIdempotencyStore" />, so requests
    ///     implementing <c>IIdempotentRequest</c> are deduplicated. The store is NOT durable — claims live in process
    ///     memory and are lost on restart — so it deduplicates only within a single process lifetime; use a database-
    ///     or Redis-backed store for cross-process at-most-once semantics. Like every explicit store registration it
    ///     <b>replaces</b> the idempotency store already registered, so the last explicit choice wins. Prefer
    ///     <c>UseIdempotency(i =&gt; i.UseInMemoryStore())</c>, which turns on the behavior and registers this store together.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">An optional action to configure the in-memory idempotency store options.</param>
    /// <returns>The service collection so that additional calls can be chained.</returns>
    public static IServiceCollection AddInMemoryIdempotencyStore(
        this IServiceCollection services,
        Action<InMemoryIdempotencyStoreOptions>? configure = null)
    {
        AddInMemoryIdempotencyStoreOptions(services, configure);
        services.RemoveAll<IIdempotencyStore>();
        services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();

        return services;
    }

    // The builder's default for a bare UseIdempotency(): the in-memory store, only while no idempotency store is
    // registered. A store registered earlier is kept, and one registered later replaces this.
    internal static void AddInMemoryIdempotencyStoreFallback(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(IIdempotencyStore))) return;

        AddInMemoryIdempotencyStoreOptions(services, null);
        services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
    }

    private static void AddInMemoryIdempotencyStoreOptions(IServiceCollection services, Action<InMemoryIdempotencyStoreOptions>? configure)
    {
        AddValidatedOptions<InMemoryIdempotencyStoreOptions, InMemoryIdempotencyStoreOptionsValidator>(services);
        if (configure is not null) services.Configure(configure);

        services.TryAddSingleton(TimeProvider.System);
    }

    /// <summary>
    ///     Registers <typeparamref name="TSerializer" /> as the application's one <see cref="INotificationSerializer" />
    ///     (a singleton), replacing the source-generated serializer completely, whether this runs before or after
    ///     <c>AddCqrsGenerated</c>. From then on it alone decides which notifications are durable and under which names:
    ///     while an outbox mode is active, a notification its <see cref="INotificationSerializer.TryGetNotificationName" />
    ///     names is stored in the outbox under that name, and any other is dispatched in-process. The generated
    ///     serializers are not consulted, so it must name, serialize and deserialize every notification that should
    ///     be durable, the <see cref="NotificationNameAttribute" /> ones included. A second call replaces the first.
    ///     Compatible with trimming and Native AOT.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <typeparam name="TSerializer">The serializer, resolved from the container once.</typeparam>
    /// <returns>The service collection so that additional calls can be chained.</returns>
    public static IServiceCollection AddNotificationSerializer<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSerializer>(
        this IServiceCollection services)
        where TSerializer : class, INotificationSerializer
    {
        ArgumentNullException.ThrowIfNull(services);

        // Not a plain Add: an already registered generated serializer must not linger beside this one as a second
        // answer to what is durable. When AddCqrsGenerated runs later, its TryAdd leaves this registration alone.
        services.RemoveAll<INotificationSerializer>();
        services.AddSingleton<INotificationSerializer, TSerializer>();
        return services;
    }
}
