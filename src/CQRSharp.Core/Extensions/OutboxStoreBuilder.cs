using CQRSharp.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CQRSharp.Pipelines;

/// <summary>
///     Selects the outbox mode and store in a single place, used by the fluent builder's <c>UseOutbox(...)</c> verb so
///     enabling the outbox is one cohesive step instead of a mode flag plus a separately-registered store. Integration
///     packages add their own store verbs (e.g. <c>UseEntityFrameworkCore&lt;TContext&gt;()</c>, <c>UseRedis(...)</c>)
///     as extension methods over the public <see cref="UseStore" /> hook. A chosen store replaces whatever outbox and
///     inbox stores are already registered. When no store is chosen, the in-memory store is used only if no outbox store
///     is registered at all — before or after this builder runs, a store registered explicitly (<c>AddRedisOutboxStore</c>,
///     <c>AddEntityFrameworkCoreOutboxStore</c>, ...) wins — so a bare <c>UseOutbox(...)</c> is a working (development)
///     configuration that never shadows a durable store.
/// </summary>
public sealed class OutboxStoreBuilder
{
    // Created by the fluent builder, which is the only thing that applies it.
    internal OutboxStoreBuilder()
    {
    }

    private Action<IServiceCollection>? _storeRegistration;
    private Action<OutboxProcessorOptions>? _processorConfig;

    /// <summary>The outbox mode this builder applies. Defaults to <see cref="OutboxMode.Enabled" />.</summary>
    internal OutboxMode Mode { get; private set; } = OutboxMode.Enabled;

    /// <summary>
    ///     Sends notifications to the outbox only when published while the scope's unit of work has an active
    ///     transaction; any other is dispatched in-process at once. Needs a unit of work (the builder's
    ///     <c>UseUnitOfWork(...)</c>): without one, nothing ever reaches the outbox.
    /// </summary>
    public OutboxStoreBuilder Transactional()
    {
        Mode = OutboxMode.Transactional;
        return this;
    }

    /// <summary>
    ///     Sends every notification the serializer names through the outbox for deferred processing: buffered during a
    ///     request (or an outbox delivery) and stored when it succeeds, or stored at once outside any. This is the default.
    /// </summary>
    public OutboxStoreBuilder Enabled()
    {
        Mode = OutboxMode.Enabled;
        return this;
    }

    /// <summary>
    ///     Uses the in-process, non-durable in-memory outbox store (development, tests, single-node demos only;
    ///     messages are lost on restart).
    /// </summary>
    /// <param name="configure">An optional action to configure the in-memory store options.</param>
    /// <returns>The same builder, for chaining.</returns>
    public OutboxStoreBuilder UseInMemoryStore(Action<InMemoryOutboxStoreOptions>? configure = null)
        => UseStore(services => services.AddInMemoryOutboxStore(configure));

    /// <summary>
    ///     Registers the outbox store via the given service-collection callback. This is the extension hook the
    ///     integration packages build on. The last store chosen on this builder is the one registered, and it replaces
    ///     every outbox and inbox store already in the collection (the in-memory fallback included): they are removed
    ///     before <paramref name="registerStore" /> runs, so a callback that uses <c>TryAdd</c> still takes effect.
    /// </summary>
    /// <param name="registerStore">
    ///     A callback that registers an <see cref="IOutboxStore" /> implementation, and the <see cref="IInboxStore" />
    ///     that pairs with it when it has one.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    public OutboxStoreBuilder UseStore(Action<IServiceCollection> registerStore)
    {
        _storeRegistration = registerStore ?? throw new ArgumentNullException(nameof(registerStore));
        return this;
    }

    /// <summary>
    ///     Tunes the outbox processor (polling interval, batch size, attempt budget, back-off). Repeated calls compose, in
    ///     call order, across every <c>UseOutbox</c> call that configures this builder.
    /// </summary>
    /// <param name="configure">An action that configures the processor options.</param>
    /// <returns>The same builder, for chaining.</returns>
    public OutboxStoreBuilder ConfigureProcessor(Action<OutboxProcessorOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _processorConfig += configure;
        return this;
    }

    // Registers the store and the processor options. The mode is applied by the builder through OutboxOptions; the
    // processor itself is registered by AddCqrs unconditionally and reads the effective mode when the host starts.
    internal void Apply(IServiceCollection services)
    {
        if (_storeRegistration is not null)
        {
            services.RemoveAll<IOutboxStore>();
            services.RemoveAll<IInboxStore>();
            _storeRegistration(services);
        }
        else
        {
            DependencyInjectionExtensions.AddInMemoryOutboxStoreFallback(services);
        }

        if (_processorConfig is not null)
            services.Configure(_processorConfig);
    }
}
