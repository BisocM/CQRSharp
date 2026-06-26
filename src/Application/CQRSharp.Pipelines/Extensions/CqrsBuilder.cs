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
///     The type and its <see cref="Build" /> entry point are public so the source-generated fluent overload (emitted
///     into the consumer assembly) can construct it with <c>useGenerated</c> forced on and drive the build. No other
///     internals are exposed.
/// </remarks>
public sealed class CqrsBuilder : ICqrsBuilder
{
    private readonly CqrsPipelinePackOptions _pack = new();

    private Action<BackgroundTaskQueueOptions>? _configureQueue;
    private Action<OutboxOptions>? _configureOutbox;
    private Action<DispatcherOptions>? _configureDispatcher;

    private bool _validateOnStart;
    private bool _useGenerated;

    // The pack is only registered if at least one pack-related verb was used; this flag tracks that so a builder
    // that configures nothing pack-related does not pull in the (idempotent, but still unnecessary) pack marker.
    private bool _usePipelinePack;

    private Func<IServiceProvider, TimeProvider>? _timeProviderFactory;

    private bool _useInMemoryOutbox;
    private Action<InMemoryOutboxStoreOptions>? _configureInMemoryOutbox;

    private bool _useInMemoryIdempotency;
    private Action<InMemoryIdempotencyStoreOptions>? _configureInMemoryIdempotency;

    private bool _useIdempotency;

    /// <summary>
    ///     Creates a builder over <paramref name="services" />. <paramref name="useGenerated" /> forces the
    ///     "apply generated registrations" intent on from the start; the generated entry point passes <c>true</c> here.
    /// </summary>
    public CqrsBuilder(IServiceCollection services, bool useGenerated = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
        _useGenerated = useGenerated;
    }

    /// <summary>
    ///     True when <see cref="UseGenerated" /> was called (or the builder was constructed with <c>useGenerated</c>).
    ///     The generated entry point reads this to decide whether to apply the generated registrations.
    /// </summary>
    public bool ShouldUseGenerated => _useGenerated;

    /// <inheritdoc />
    public IServiceCollection Services { get; }

    /// <inheritdoc />
    public ICqrsBuilder UseGenerated()
    {
        _useGenerated = true;
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder ConfigureQueue(Action<BackgroundTaskQueueOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configureQueue = configure;
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder ConfigureOutbox(Action<OutboxOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configureOutbox = configure;
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
    public ICqrsBuilder ValidateOnStart(bool enabled = true)
    {
        _validateOnStart = enabled;
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
    public ICqrsBuilder UseValidation()
    {
        _usePipelinePack = true;
        _pack.IncludeValidation = true;
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder UseExceptionHandling()
    {
        _usePipelinePack = true;
        _pack.IncludeExceptionHandling = true;
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
    public ICqrsBuilder UseIdempotency()
    {
        _useIdempotency = true;
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

    /// <inheritdoc />
    public ICqrsBuilder UseInMemoryOutbox(Action<InMemoryOutboxStoreOptions>? configure = null)
    {
        _useInMemoryOutbox = true;
        _configureInMemoryOutbox = configure;
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder UseInMemoryIdempotency(Action<InMemoryIdempotencyStoreOptions>? configure = null)
    {
        _useInMemoryIdempotency = true;
        _configureInMemoryIdempotency = configure;
        return this;
    }

    /// <summary>
    ///     Applies the accumulated intent in one fixed canonical sequence, independent of the order the verbs were
    ///     called: core <c>AddCqrs</c> first, then the authoritative <see cref="TimeProvider" /> override, then the
    ///     in-memory outbox store, then the merged pipeline pack. Generated registrations are NOT applied here — the
    ///     generated entry point owns that step so it can order it correctly against the generated dispatchers'
    ///     <c>RemoveAll</c>-authoritative wiring.
    /// </summary>
    public IServiceCollection Build()
    {
        // 1) Core services. A disabled validator maps to the Off policy; otherwise leave the AddCqrs default
        //    (ThrowOnError) so enabling it is just "don't turn it off".
        Services.AddCqrs(
            _configureQueue,
            _configureOutbox,
            _configureDispatcher,
            _validateOnStart
                ? null
                : opts => opts.Policy = CqrsValidationPolicy.Off);

        // 2) Authoritative clock seam. AddCqrs only TryAdds TimeProvider.System, so without this a consumer override
        //    would lose to whatever ran first; RemoveAll + AddSingleton makes the chosen provider win regardless of
        //    where UseTimeProvider appeared in the chain.
        if (_timeProviderFactory is not null)
        {
            Services.RemoveAll<TimeProvider>();
            Services.AddSingleton(_timeProviderFactory);
        }

        // 3) In-memory stores, if requested.
        if (_useInMemoryOutbox)
            Services.AddInMemoryOutboxStore(_configureInMemoryOutbox);

        if (_useInMemoryIdempotency)
            Services.AddInMemoryIdempotencyStore(_configureInMemoryIdempotency);

        // 4) Idempotency behavior, if requested (not part of the pack's option surface).
        if (_useIdempotency)
            Services.AddIdempotency();

        // 5) The merged pipeline pack. Registered only when a pack-related verb was used; the pack's marker keeps it
        //    idempotent against any earlier AddCqrsPipelinePack call.
        if (_usePipelinePack)
            Services.AddCqrsPipelinePack(ConfigurePack);

        return Services;
    }

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
