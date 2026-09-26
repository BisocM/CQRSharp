using CQRSharp.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CQRSharp.Pipelines;

/// <summary>
///     The <see cref="ICqrsBuilder" /> behind <c>AddCqrsGenerated(Action&lt;ICqrsBuilder&gt;)</c>. Each verb only records
///     intent; <see cref="Build" /> applies it in one fixed sequence, so the order of different verbs never changes what is
///     registered. Every registration <see cref="Build" /> makes is additive, which is what lets two builder calls on one
///     service collection (a library's own wiring and its host's) both take effect.
/// </summary>
internal sealed class CqrsBuilder(IServiceCollection services) : ICqrsBuilder
{
    private Action<BackgroundTaskQueueOptions>? _configureQueue;
    private Action<DispatcherOptions>? _configureDispatcher;
    private Action<NotificationOptions>? _configureNotifications;

    // Null until ValidateOnStart is called, so a builder that never asked leaves the policy to whoever set it (another
    // builder call, a Configure<CqrsStartupValidationOptions>, a configuration binding) or, when nobody did, to the host
    // environment.
    private CqrsValidationPolicy? _validationPolicy;

    private Func<IServiceProvider, TimeProvider>? _timeProviderFactory;

    private bool _validation = true;
    private bool _exceptionHandling = true;
    private bool _logging;
    private Action<RateLimitingOptions>? _configureRateLimiting;
    private Action<ResilienceOptions>? _configureResilience;
    private Action<TimeoutOptions>? _configureTimeout;
    private Func<IServiceProvider, IUnitOfWork>? _unitOfWorkFactory;
    private Action<UnitOfWorkOptions>? _configureUnitOfWork;

    private OutboxStoreBuilder? _outbox;
    private IdempotencyStoreBuilder? _idempotency;

    /// <inheritdoc />
    public IServiceCollection Services { get; } = services ?? throw new ArgumentNullException(nameof(services));

    /// <inheritdoc />
    public ICqrsBuilder ConfigureQueue(Action<BackgroundTaskQueueOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configureQueue += configure;
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder ConfigureDispatcher(Action<DispatcherOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configureDispatcher += configure;
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder ConfigureNotifications(Action<NotificationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configureNotifications += configure;
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
        "host already registered, don't call UseTimeProvider at all: AddCqrsGenerated registers TimeProvider.System " +
        "with TryAdd, so an existing registration wins.";

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
    public ICqrsBuilder UseLogging()
    {
        _logging = true;
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder UseValidation(bool enabled = true)
    {
        _validation = enabled;
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder UseExceptionHandling(bool enabled = true)
    {
        _exceptionHandling = enabled;
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder UseRateLimiting(Action<RateLimitingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configureRateLimiting += configure;
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder UseResilience(Action<ResilienceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configureResilience += configure;
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder UseTimeout(Action<TimeoutOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configureTimeout += configure;
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder UseOutbox(Action<OutboxStoreBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_outbox ??= new OutboxStoreBuilder());
        return this;
    }

    /// <inheritdoc />
    public ICqrsBuilder UseIdempotency(Action<IdempotencyStoreBuilder>? configure = null)
    {
        _idempotency ??= new IdempotencyStoreBuilder();
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

        // An upcast (TUnitOfWork : IUnitOfWork), so no reflection is involved.
        _unitOfWorkFactory = sp => factory(sp);
        _configureUnitOfWork += configure;
        return this;
    }

    /// <summary>
    ///     Applies the recorded intent: the core services first, then the options the verbs configured, the authoritative
    ///     clock, the outbox and idempotency stores, and finally the behaviors. The source-generated registrations are
    ///     applied afterwards by the generated entry point, so their authoritative dispatcher registrations win.
    /// </summary>
    public void Build()
    {
        Services.AddCqrs();

        if (_configureQueue is not null)
            Services.Configure(_configureQueue);
        if (_configureDispatcher is not null)
            Services.Configure(_configureDispatcher);
        if (_configureNotifications is not null)
            Services.Configure(_configureNotifications);
        if (_validationPolicy is { } policy)
            Services.Configure<CqrsStartupValidationOptions>(options => options.Policy = policy);

        // AddCqrs only TryAdds TimeProvider.System, so the chosen provider replaces whatever was registered before.
        if (_timeProviderFactory is { } timeProviderFactory)
        {
            Services.RemoveAll<TimeProvider>();
            Services.AddSingleton<TimeProvider>(sp => ResolveTimeProviderGuarded(timeProviderFactory, sp));
        }

        // A store chosen through UseOutbox or UseIdempotency replaces any registered one; with none chosen, the in-memory
        // store is registered only when no store is registered at all, so one registered outside the builder wins in
        // either order.
        if (_outbox is { } outbox)
        {
            Services.Configure<OutboxOptions>(options => options.Mode = outbox.Mode);
            outbox.Apply(Services);
        }

        if (_idempotency is { } idempotency)
        {
            Services.AddIdempotency();
            idempotency.Apply(Services);
        }

        if (_exceptionHandling)
            Services.AddExceptionHandling();
        if (_validation)
            Services.AddValidationBehavior();
        if (_logging)
            Services.AddLoggingBehavior();
        if (_configureRateLimiting is not null)
            Services.AddRateLimiting(_configureRateLimiting);
        if (_unitOfWorkFactory is not null)
            Services.AddUnitOfWorkBehavior(_unitOfWorkFactory, _configureUnitOfWork);
        if (_configureTimeout is not null)
            Services.AddTimeoutBehavior(_configureTimeout);
        if (_configureResilience is not null)
            Services.AddResilienceBehavior(_configureResilience);
    }
}
