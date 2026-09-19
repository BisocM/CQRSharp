using System;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Pipelines;

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
    private Action<IServiceCollection>? _serializerRegistration;

    /// <summary>
    ///     Replays value-carrying results (<c>CommandResult&lt;T&gt;</c>, query results) to duplicate requests, serialized
    ///     with System.Text.Json through the type metadata <paramref name="options" /> resolve. For Native AOT, pass
    ///     options whose <c>TypeInfoResolver</c> is your source-generated <c>JsonSerializerContext</c>; a result type the
    ///     options cannot resolve is not replayed (its duplicate is rejected with <c>DuplicateRequestException</c>).
    /// </summary>
    /// <remarks>A plain <c>CommandResult</c> is always replayed and needs none of this.</remarks>
    /// <param name="options">The JSON options (and type metadata) to serialize results with.</param>
    /// <returns>The same builder, for chaining.</returns>
    public IdempotencyStoreBuilder ReplayResultsWith(System.Text.Json.JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ReplayResultsWith(new Core.Idempotency.JsonIdempotencyResultSerializer(options));
    }

    /// <summary>Replays value-carrying results to duplicate requests using a custom serializer.</summary>
    /// <param name="serializer">The serializer that stores and restores results.</param>
    /// <returns>The same builder, for chaining.</returns>
    public IdempotencyStoreBuilder ReplayResultsWith(IIdempotencyResultSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        _serializerRegistration = services => services.AddSingleton(serializer);
        return this;
    }

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

        _serializerRegistration?.Invoke(services);
    }
}
