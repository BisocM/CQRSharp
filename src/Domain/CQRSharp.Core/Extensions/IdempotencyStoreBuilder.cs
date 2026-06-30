using System;
using CQRSharp.Core.Options;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Extensions;

/// <summary>
///     Selects the idempotency store, used by the fluent builder's <c>UseIdempotency(...)</c> verb so enabling
///     at-most-once processing is one cohesive step (behavior plus store) instead of two. Integration packages add
///     their own store verbs (e.g. <c>UseEntityFrameworkCore&lt;TContext&gt;()</c>, <c>UseRedis(...)</c>) as extension
///     methods over the public <see cref="UseStore" /> hook. When no store is chosen, the in-memory store is used, so a
///     bare <c>UseIdempotency()</c> works out of the box (in development).
/// </summary>
public sealed class IdempotencyStoreBuilder
{
    private Action<IServiceCollection>? _storeRegistration;

    /// <summary>
    ///     Uses the in-process, non-durable in-memory idempotency store (development, tests, single-node demos only;
    ///     claims are lost on restart, so it deduplicates within a single process lifetime).
    /// </summary>
    public IdempotencyStoreBuilder UseInMemoryStore(Action<InMemoryIdempotencyStoreOptions>? configure = null)
        => UseStore(services => services.AddInMemoryIdempotencyStore(configure));

    /// <summary>
    ///     Registers the idempotency store via the given service-collection callback. This is the extension hook the
    ///     integration packages build on. Replaces any previously selected store.
    /// </summary>
    /// <param name="registerStore">A callback that registers an <c>IIdempotencyStore</c> implementation.</param>
    public IdempotencyStoreBuilder UseStore(Action<IServiceCollection> registerStore)
    {
        _storeRegistration = registerStore ?? throw new ArgumentNullException(nameof(registerStore));
        return this;
    }

    // Registers the chosen store, defaulting to the in-memory store when none was chosen.
    internal void Apply(IServiceCollection services)
    {
        if (_storeRegistration is not null)
            _storeRegistration(services);
        else
            services.AddInMemoryIdempotencyStore();
    }
}
