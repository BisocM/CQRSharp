using CQRSharp.Abstractions.Interfaces.Transactions;
using CQRSharp.Core.Extensions;
using CQRSharp.Core.Options;
using CQRSharp.Core.Options.Enums;
using CQRSharp.Pipelines.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CQRSharp.Pipelines.Extensions;

/// <summary>
///     The fluent builder implementation. Each verb records intent into a field or into the single
///     <see cref="CqrsPipelinePackOptions" /> accumulator; nothing is registered until <see cref="Build" /> runs the
///     one fixed canonical sequence. This is what makes the public surface order-insensitive: the order verbs are
///     called in never changes what the build does. It is sealed and field-access-only so it stays AOT-clean.
/// </summary>
/// <remarks>
///     The type and its <see cref="Build" /> entry point are public so the source-generated entry point
///     <c>AddCqrsGenerated(Action&lt;ICqrsBuilder&gt;)</c> (emitted into the consumer assembly) can construct it and
///     drive the build before applying the generated registrations. No other internals are exposed.
/// </remarks>
public sealed class CqrsBuilder : ICqrsBuilder
{
    private readonly CqrsPipelinePackOptions _pack = new();

    private Action<BackgroundTaskQueueOptions>? _configureQueue;
    private Action<DispatcherOptions>? _configureDispatcher;
    private Action<NotificationOptions>? _configureNotifications;

    private CqrsValidationPolicy _validationPolicy = CqrsValidationPolicy.Off;

    // The pack is only registered if at least one pack-related verb was used; this flag tracks that so a builder
    // that configures nothing pack-related does not pull in the (idempotent, but still unnecessary) pack marker.
    private bool _usePipelinePack;

    private Func<IServiceProvider, TimeProvider>? _timeProviderFactory;

    private OutboxStoreBuilder? _outbox;
    private IdempotencyStoreBuilder? _idempotency;

    /// <summary>Creates a builder over <paramref name="services" />.</summary>
    public CqrsBuilder(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
    }

    /// <inheritdoc />
    public IServiceCollection Services { get; }

    /// <inheritdoc />
    public ICqrsBuilder ConfigureQueue(Action<BackgroundTaskQueueOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configureQueue = configure;
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder ConfigureDispatcher(Action<DispatcherOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configureDispatcher = configure;
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder ConfigureNotifications(Action<NotificationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configureNotifications = configure;
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder ValidateOnStart(bool enabled = true)
    {
        _validationPolicy = enabled ? CqrsValidationPolicy.ThrowOnError : CqrsValidationPolicy.Off;
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder ValidateOnStart(CqrsValidationPolicy policy)
    {
        _validationPolicy = policy;
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder UseTimeProvider(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProviderFactory = _ => timeProvider;
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder UseTimeProvider(Func<IServiceProvider, TimeProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _timeProviderFactory = factory;
        return this;
    }

    // A UseTimeProvider factory is registered AS the TimeProvider service, so a factory that resolves TimeProvider from
    // the provider (e.g. sp.GetService<TimeProvider>()) re-enters itself and recurses until the container deadlocks.
    // This sentinel + wrapper turn that misconfiguration into a fast, clear failure on first resolution. Thread-static
    // because singleton resolution is synchronous on one thread; the nested resolution re-enters on that same thread.
    [ThreadStatic] private static bool _resolvingTimeProvider;

    private const string SelfReferentialTimeProviderMessage =
        "The Func<IServiceProvider, TimeProvider> passed to UseTimeProvider resolves TimeProvider from the service " +
        "provider (e.g. sp.GetService<TimeProvider>()). That factory is itself the TimeProvider registration, so " +
        "resolving TimeProvider inside it recurses until the container deadlocks. Return a concrete TimeProvider instead " +
        "(TimeProvider.System, a FakeTimeProvider, or your own clock). To make CQRSharp defer to a TimeProvider your " +
        "host already registered, don't call UseTimeProvider at all: AddCqrs registers TimeProvider.System with TryAdd, " +
        "so an existing registration wins.";

    private static TimeProvider ResolveTimeProviderGuarded(Func<IServiceProvider, TimeProvider> factory, IServiceProvider sp)
    {
        if (_resolvingTimeProvider)
            throw new InvalidOperationException(SelfReferentialTimeProviderMessage);

        _resolvingTimeProvider = true;
        try
        {
            return factory(sp) ?? throw new InvalidOperationException(
                "The Func<IServiceProvider, TimeProvider> passed to UseTimeProvider returned null. Return a non-null " +
                "TimeProvider (e.g. TimeProvider.System or your own clock).");
        }
        finally
        {
            _resolvingTimeProvider = false;
        }
    }

    /// <inheritdoc />
    public ICqrsBuilder UsePipelinePack(Action<CqrsPipelinePackOptions>? configure = null)
    {
        _usePipelinePack = true;
        configure?.Invoke(_pack);
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder UseLogging()
    {
        _usePipelinePack = true;
        _pack.IncludeLogging = true;
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder UseValidation(bool enabled = true)
    {
        _usePipelinePack = true;
        _pack.IncludeValidation = enabled;
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder UseExceptionHandling(bool enabled = true)
    {
        _usePipelinePack = true;
        _pack.IncludeExceptionHandling = enabled;
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder UseRateLimiting(Action<RateLimiterOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _usePipelinePack = true;
        _pack.ConfigureRateLimiting = configure;
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder UseResilience(Action<ResilienceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _usePipelinePack = true;
        _pack.ConfigureResilience = configure;
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder UseTimeout(Action<TimeoutOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _usePipelinePack = true;
        _pack.ConfigureTimeout = configure;
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder UseOutbox(Action<OutboxStoreBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _outbox = new OutboxStoreBuilder();
        configure(_outbox);
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder UseIdempotency(Action<IdempotencyStoreBuilder>? configure = null)
    {
        _idempotency = new IdempotencyStoreBuilder();
        configure?.Invoke(_idempotency);
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder UseUnitOfWork<TUnitOfWork>(
        Func<IServiceProvider, TUnitOfWork> factory,
        Action<UnitOfWorkOptions>? configure = null)
        where TUnitOfWork : class, IUnitOfWork
    {
        ArgumentNullException.ThrowIfNull(factory);
        _usePipelinePack = true;

        // Adapt the concrete factory to the IUnitOfWork-typed slot the pack exposes. The cast is an upcast (the
        // generic constraint guarantees TUnitOfWork : IUnitOfWork), so it is AOT-safe — no reflection.
        _pack.UnitOfWorkFactory = sp => factory(sp);
        _pack.ConfigureUnitOfWork = configure;
        return this;
    }

    /// <summary>
    ///     Applies the accumulated intent in one fixed canonical sequence, independent of the order the verbs were
    ///     called: core <c>AddCqrs</c> first, then the authoritative <see cref="TimeProvider" /> override, then the
    ///     outbox store/processor and the idempotency behavior/store, then the merged pipeline pack. Generated
    ///     registrations are NOT applied here — the generated entry point owns that step so it can order it correctly
    ///     against the generated dispatchers' <c>RemoveAll</c>-authoritative wiring.
    /// </summary>
    public IServiceCollection Build()
    {
        // 1) Core services. The outbox mode comes from UseOutbox (off — Disabled — when it was not used), and AddCqrs
        //    registers the outbox processor host service off that mode. The startup-validator policy is whatever
        //    ValidateOnStart(...) recorded; the builder path defaults to Off (call ValidateOnStart() to turn it on).
        Services.AddCqrs(
            _configureQueue,
            _outbox is not null ? OutboxModeConfigurator : null,
            _configureDispatcher,
            opts => opts.Policy = _validationPolicy);

        // 1b) Notification dispatch options (publish strategy). Applied directly; the Options default (Sequential) is
        //     used when this verb was not called.
        if (_configureNotifications is not null)
            Services.Configure(_configureNotifications);

        // 2) Authoritative clock seam. AddCqrs only TryAdds TimeProvider.System, so without this a consumer override
        //    would lose to whatever ran first; RemoveAll + AddSingleton makes the chosen provider win regardless of
        //    where UseTimeProvider appeared in the chain.
        if (_timeProviderFactory is not null)
        {
            var factory = _timeProviderFactory;
            Services.RemoveAll<TimeProvider>();
            // Register the factory guarded: a self-referential factory (one that resolves TimeProvider from the
            // provider) fails fast with a clear message on first resolution instead of deadlocking the container.
            Services.AddSingleton<TimeProvider>(sp => ResolveTimeProviderGuarded(factory, sp));
        }

        // 3) Outbox store + processor options (the processor host service was registered by AddCqrs off the mode set
        //    in step 1). Defaults to the in-memory store when no integration store was chosen.
        _outbox?.Apply(Services);

        // 4) Idempotency behavior + store (defaults to the in-memory store when none was chosen).
        if (_idempotency is not null)
        {
            Services.AddIdempotency();
            _idempotency.Apply(Services);
        }

        // 5) The merged pipeline pack. Registered only when a pack-related verb was used; the pack's marker keeps it
        //    idempotent against any earlier AddCqrsPipelinePack call.
        if (_usePipelinePack)
            Services.AddCqrsPipelinePack(ConfigurePack);

        return Services;
    }

    // Applies the outbox mode selected by UseOutbox into OutboxOptions so AddCqrs registers the processor accordingly.
    private void OutboxModeConfigurator(OutboxOptions options) => options.Mode = _outbox!.Mode;

    // Copies the accumulator into the pack options the registration call hands us. Done as a method (not a captured
    // lambda over the accumulator directly) so the single canonical pack is applied verbatim.
    private void ConfigurePack(CqrsPipelinePackOptions options)
    {
        options.IncludeExceptionHandling = _pack.IncludeExceptionHandling;
        options.IncludeValidation = _pack.IncludeValidation;
        options.IncludeLogging = _pack.IncludeLogging;
        options.UnitOfWorkFactory = _pack.UnitOfWorkFactory;
        options.ConfigureUnitOfWork = _pack.ConfigureUnitOfWork;
        options.ConfigureRateLimiting = _pack.ConfigureRateLimiting;
        options.ConfigureResilience = _pack.ConfigureResilience;
        options.ConfigureTimeout = _pack.ConfigureTimeout;
    }
}
