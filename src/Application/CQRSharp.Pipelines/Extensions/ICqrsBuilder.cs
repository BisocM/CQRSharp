using CQRSharp.Abstractions.Interfaces.Transactions;
using CQRSharp.Core.Extensions;
using CQRSharp.Core.Options;
using CQRSharp.Pipelines.Options;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Pipelines.Extensions;

/// <summary>
///     The fluent builder for wiring CQRSharp into a service collection, reached through
///     <c>AddCqrsGenerated(Action&lt;ICqrsBuilder&gt;)</c> (which also applies the source-generated registrations).
///     Every verb only <em>records intent</em>; the accumulated configuration is applied in one fixed, canonical
///     sequence when the builder is built. Because of that, the order the verbs are called in is irrelevant — the same
///     registrations and the same resolved pipeline result whatever order they appear in. Each verb returns the builder
///     so calls can be chained.
/// </summary>
public interface ICqrsBuilder
{
    /// <summary>
    ///     The underlying service collection. An escape hatch for registrations the builder does not model directly
    ///     (your handlers' dependencies, custom behaviors, alternative stores, etc.). Registrations made here are
    ///     applied immediately, so — unlike the builder's own verbs — their effect <em>can</em> depend on ordering
    ///     relative to other direct <c>Services</c> calls, exactly as with any plain service-collection usage.
    /// </summary>
    IServiceCollection Services { get; }

    /// <summary>
    ///     Configures the background task queue. Order-insensitive: the delegate is stored and applied during the build.
    /// </summary>
    ICqrsBuilder ConfigureQueue(Action<BackgroundTaskQueueOptions> configure);

    /// <summary>
    ///     Configures dispatcher options (run mode and scope mode). Order-insensitive: the delegate is stored and
    ///     applied during the build.
    /// </summary>
    ICqrsBuilder ConfigureDispatcher(Action<DispatcherOptions> configure);

    /// <summary>
    ///     Configures notification dispatch options (the publish strategy). Order-insensitive: the delegate is stored
    ///     and applied during the build.
    /// </summary>
    ICqrsBuilder ConfigureNotifications(Action<NotificationOptions> configure);

    /// <summary>
    ///     Enables (or disables) the fail-fast startup validator. Order-insensitive: it records the chosen validation
    ///     policy, which the build applies. Enabled maps to <c>ThrowOnError</c> (abort host start on a configuration
    ///     error); disabled maps to <c>Off</c>.
    /// </summary>
    ICqrsBuilder ValidateOnStart(bool enabled = true);

    /// <summary>
    ///     Sets the authoritative <see cref="TimeProvider" /> (the clock seam every time-dependent component reads).
    ///     Order-insensitive and last-writer-wins by design: the build removes any prior registration and registers
    ///     this one, so the provider set here is used regardless of where this verb appears in the chain.
    /// </summary>
    ICqrsBuilder UseTimeProvider(TimeProvider timeProvider);

    /// <summary>
    ///     Sets the authoritative <see cref="TimeProvider" /> via a factory resolved from the service provider.
    ///     Order-insensitive and last-writer-wins, exactly as <see cref="UseTimeProvider(TimeProvider)" />.
    /// </summary>
    ICqrsBuilder UseTimeProvider(Func<IServiceProvider, TimeProvider> factory);

    /// <summary>
    ///     Configures the optional pipeline pack. Order-insensitive: the delegate mutates a single shared
    ///     <see cref="CqrsPipelinePackOptions" /> accumulator that all pack-related verbs feed, and the merged result
    ///     is registered once during the build (its marker keeps that idempotent). Passing <c>null</c> registers the
    ///     pack with its defaults.
    /// </summary>
    ICqrsBuilder UsePipelinePack(Action<CqrsPipelinePackOptions>? configure = null);

    /// <summary>
    ///     Enables the per-request logging behavior. Order-insensitive: it sets the pack accumulator's logging flag.
    /// </summary>
    ICqrsBuilder UseLogging();

    /// <summary>
    ///     Enables the validation behavior (runs every registered request validator). Order-insensitive: it sets the
    ///     pack accumulator's validation flag.
    /// </summary>
    ICqrsBuilder UseValidation();

    /// <summary>
    ///     Enables request-level exception-hook support. Order-insensitive: it sets the pack accumulator's
    ///     exception-handling flag.
    /// </summary>
    ICqrsBuilder UseExceptionHandling();

    /// <summary>
    ///     Enables and configures the rate-limiting behavior. Order-insensitive: it stores the configuration on the
    ///     pack accumulator.
    /// </summary>
    ICqrsBuilder UseRateLimiting(Action<RateLimiterOptions> configure);

    /// <summary>
    ///     Enables and configures the resilience/retry behavior. Order-insensitive: it stores the configuration on the
    ///     pack accumulator.
    /// </summary>
    ICqrsBuilder UseResilience(Action<ResilienceOptions> configure);

    /// <summary>
    ///     Enables and configures the timeout behavior. Order-insensitive: it stores the configuration on the pack
    ///     accumulator.
    /// </summary>
    ICqrsBuilder UseTimeout(Action<TimeoutOptions> configure);

    /// <summary>
    ///     Enables the notification outbox in one cohesive step: selects the mode (<c>Transactional</c> by default),
    ///     registers the store (the in-memory store unless an integration store is chosen via
    ///     <see cref="OutboxStoreBuilder" />), and ensures the outbox processor runs. Order-insensitive: the selection
    ///     is recorded and applied during the build. Not calling this leaves the outbox off.
    /// </summary>
    ICqrsBuilder UseOutbox(Action<OutboxStoreBuilder> configure);

    /// <summary>
    ///     Enables idempotency (at-most-once processing for requests implementing <c>IIdempotentRequest</c>) in one
    ///     cohesive step: turns on the behavior and registers the store. With no configuration the in-memory store is
    ///     used; choose a durable store via the <see cref="IdempotencyStoreBuilder" /> (e.g.
    ///     <c>i => i.UseRedis(...)</c>). Order-insensitive: it records the intent and applies it during the build.
    /// </summary>
    ICqrsBuilder UseIdempotency(Action<IdempotencyStoreBuilder>? configure = null);

    /// <summary>
    ///     Enables and configures the unit-of-work behavior with an AOT-safe factory for the concrete unit of work.
    ///     Order-insensitive: it stores the factory and options on the pack accumulator.
    /// </summary>
    ICqrsBuilder UseUnitOfWork<TUnitOfWork>(
        Func<IServiceProvider, TUnitOfWork> factory,
        Action<UnitOfWorkOptions>? configure = null)
        where TUnitOfWork : class, IUnitOfWork;
}
