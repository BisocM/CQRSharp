using CQRSharp.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CQRSharp.Pipelines;

/// <summary>
///     Selects the idempotency store, used by the fluent builder's <c>UseIdempotency(...)</c> verb so enabling
///     at-most-once processing is one cohesive step (behavior plus store) instead of two. Integration packages add
///     their own store verbs (e.g. <c>UseEntityFrameworkCore&lt;TContext&gt;()</c>, <c>UseRedis(...)</c>) as extension
///     methods over the public <see cref="UseStore" /> hook. A chosen store replaces whatever idempotency store is
///     already registered. When no store is chosen, the in-memory store is used only if no idempotency store is
///     registered at all — before or after this builder runs, a store registered explicitly
///     (<c>AddRedisIdempotencyStore</c>, <c>AddEntityFrameworkCoreIdempotencyStore</c>, ...) wins — so a bare
///     <c>UseIdempotency()</c> works out of the box (in development) and never shadows a durable store.
/// </summary>
public sealed class IdempotencyStoreBuilder
{
    // Created by the fluent builder, which is the only thing that applies it.
    internal IdempotencyStoreBuilder()
    {
    }

    private Action<IServiceCollection>? _storeRegistration;
    private Action<IServiceCollection>? _serializerRegistration;

    /// <summary>
    ///     Replays value-carrying results (<c>CommandResult&lt;T&gt;</c>, query results) to duplicate requests, serialized
    ///     with System.Text.Json through the type metadata <paramref name="options" /> resolve. The options need a
    ///     <c>TypeInfoResolver</c> on every runtime: your source-generated <c>JsonSerializerContext</c> (required for
    ///     Native AOT and trimming), or <c>new DefaultJsonTypeInfoResolver()</c> where reflection is available. A result
    ///     type the resolver cannot resolve is not replayed (its duplicate is rejected with
    ///     <c>DuplicateRequestException</c>, and a warning is logged once per result type).
    /// </summary>
    /// <remarks>A plain <c>CommandResult</c> is always replayed and needs none of this.</remarks>
    /// <param name="options">The JSON options (and type metadata) to serialize results with.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="options" /> has no <c>TypeInfoResolver</c>.</exception>
    public IdempotencyStoreBuilder ReplayResultsWith(System.Text.Json.JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Options without a resolver resolve no type at all - not even on the JIT, where JsonSerializer itself would fall
        // back to reflection - so nothing would ever be replayed, silently.
        if (options.TypeInfoResolver is null)
            throw new ArgumentException(
                "The JsonSerializerOptions have no TypeInfoResolver, so no result type can be resolved and nothing would be replayed. " +
                "Set TypeInfoResolver to your source-generated JsonSerializerContext (or to new DefaultJsonTypeInfoResolver() when not trimming).",
                nameof(options));

        return ReplayResultsWith(new Core.Idempotency.JsonIdempotencyResultSerializer(options));
    }

    /// <summary>
    ///     Replays value-carrying results to duplicate requests using a custom serializer, which replaces any result
    ///     serializer already registered.
    /// </summary>
    /// <param name="serializer">The serializer that stores and restores results.</param>
    /// <returns>The same builder, for chaining.</returns>
    public IdempotencyStoreBuilder ReplayResultsWith(IIdempotencyResultSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        _serializerRegistration = services =>
        {
            services.RemoveAll<IIdempotencyResultSerializer>();
            services.AddSingleton(serializer);
        };
        return this;
    }

    /// <summary>
    ///     Uses the in-process, non-durable in-memory idempotency store (development, tests, single-node demos only;
    ///     claims are lost on restart, so it deduplicates within a single process lifetime).
    /// </summary>
    /// <param name="configure">An optional action to configure the in-memory store options.</param>
    /// <returns>The same builder, for chaining.</returns>
    public IdempotencyStoreBuilder UseInMemoryStore(Action<InMemoryIdempotencyStoreOptions>? configure = null)
        => UseStore(services => services.AddInMemoryIdempotencyStore(configure));

    /// <summary>
    ///     Registers the idempotency store via the given service-collection callback. This is the extension hook the
    ///     integration packages build on. The last store chosen on this builder is the one registered, and it replaces
    ///     every idempotency store already in the collection (the in-memory fallback included): they are removed before
    ///     <paramref name="registerStore" /> runs, so a callback that uses <c>TryAdd</c> still takes effect.
    /// </summary>
    /// <param name="registerStore">A callback that registers an <see cref="IIdempotencyStore" /> implementation.</param>
    /// <returns>The same builder, for chaining.</returns>
    public IdempotencyStoreBuilder UseStore(Action<IServiceCollection> registerStore)
    {
        _storeRegistration = registerStore ?? throw new ArgumentNullException(nameof(registerStore));
        return this;
    }

    // Registers the chosen store, or the in-memory fallback when none was chosen.
    internal void Apply(IServiceCollection services)
    {
        if (_storeRegistration is not null)
        {
            services.RemoveAll<IIdempotencyStore>();
            _storeRegistration(services);
        }
        else
        {
            DependencyInjectionExtensions.AddInMemoryIdempotencyStoreFallback(services);
        }

        _serializerRegistration?.Invoke(services);
    }
}
