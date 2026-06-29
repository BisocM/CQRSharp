using System;
using CQRSharp.Core.Options;
using CQRSharp.Core.Options.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Extensions;

/// <summary>
///     Selects the outbox mode and store in a single place, used by the fluent builder's <c>UseOutbox(...)</c> verb so
///     enabling the outbox is one cohesive step instead of a mode flag plus a separately-registered store. Integration
///     packages add their own store verbs (e.g. <c>UseEntityFrameworkCore&lt;TContext&gt;()</c>, <c>UseRedis(...)</c>)
///     as extension methods over the public <see cref="UseStore" /> hook. When no store is chosen, the in-memory store
///     is used, so a bare <c>UseOutbox(...)</c> is a working (development) configuration.
/// </summary>
public sealed class OutboxStoreBuilder
{
    private Action<IServiceCollection>? _storeRegistration;
    private Action<OutboxProcessorOptions>? _processorConfig;

    /// <summary>The outbox mode this builder applies. Defaults to <see cref="OutboxMode.Transactional" />.</summary>
    internal OutboxMode Mode { get; private set; } = OutboxMode.Transactional;

    /// <summary>
    ///     Sends notifications to the outbox only when published inside an active unit-of-work transaction; otherwise
    ///     they are dispatched directly. This is the default when the outbox is enabled.
    /// </summary>
    public OutboxStoreBuilder Transactional()
    {
        Mode = OutboxMode.Transactional;
        return this;
    }

    /// <summary>Sends every notification through the outbox for deferred processing.</summary>
    public OutboxStoreBuilder Enabled()
    {
        Mode = OutboxMode.Enabled;
        return this;
    }

    /// <summary>
    ///     Uses the in-process, non-durable in-memory outbox store (development, tests, single-node demos only;
    ///     messages are lost on restart).
    /// </summary>
    public OutboxStoreBuilder UseInMemoryStore(Action<InMemoryOutboxStoreOptions>? configure = null)
        => UseStore(services => services.AddInMemoryOutboxStore(configure));

    /// <summary>
    ///     Registers the outbox store via the given service-collection callback. This is the extension hook the
    ///     integration packages build on. Replaces any previously selected store.
    /// </summary>
    /// <param name="registerStore">A callback that registers an <c>IOutboxStore</c> implementation.</param>
    public OutboxStoreBuilder UseStore(Action<IServiceCollection> registerStore)
    {
        _storeRegistration = registerStore ?? throw new ArgumentNullException(nameof(registerStore));
        return this;
    }

    /// <summary>Tunes the outbox processor (polling interval, batch size, retry budget).</summary>
    public OutboxStoreBuilder ConfigureProcessor(Action<OutboxProcessorOptions> configure)
    {
        _processorConfig = configure ?? throw new ArgumentNullException(nameof(configure));
        return this;
    }

    // Registers the store and processor options. The mode is applied by the builder (so AddCqrs registers the
    // processor host service off it). Defaults to the in-memory store when none was chosen.
    internal void Apply(IServiceCollection services)
    {
        if (_storeRegistration is not null)
            _storeRegistration(services);
        else
            services.AddInMemoryOutboxStore();

        if (_processorConfig is not null)
            services.Configure(_processorConfig);
    }
}
