using CQRSharp.Abstractions.Interfaces.Transactions;
using CQRSharp.Core.Options;
using CQRSharp.Pipelines.Options;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Pipelines.Extensions;

/// <summary>
///     A fluent builder for wiring CQRSharp into a service collection. Every verb only <em>records intent</em>; the
///     accumulated configuration is applied in one fixed, canonical sequence when the builder is built. Because of
///     that, the order in which these verbs are called is irrelevant — calling <see cref="UseLogging" /> before or
///     after <see cref="ConfigureQueue" /> (or before or after <see cref="UseTimeProvider(TimeProvider)" />) produces
///     the exact same registrations and the exact same resolved pipeline. Each verb returns the builder so calls can
///     be chained.
/// </summary>
public interface ICqrsBuilder
{
    /// <summary>
    ///     The underlying service collection. An escape hatch for registrations the builder does not model directly
    ///     (your handlers' dependencies, custom behaviors, alternative outbox stores, etc.). Registrations made here
    ///     are applied immediately, so — unlike the builder's own verbs — their effect <em>can</em> depend on ordering
    ///     relative to other direct <c>Services</c> calls, exactly as with any plain service-collection usage.
    /// </summary>
    IServiceCollection Services { get; }

    /// <summary>
    ///     Opts in to applying the source-generated CQRSharp registrations (the generated dispatchers, handler/registry
    ///     wiring, and outbox serializer). Order-insensitive: it sets a flag the build consumes. When the builder is
    ///     reached through the generated entry point this is implied; when reached through the plain
    ///     <c>AddCqrs(Action&lt;ICqrsBuilder&gt;)</c> entry point — where the consumer applies the generated
    ///     registrations elsewhere — this verb is a documented no-op.
    /// </summary>
    ICqrsBuilder UseGenerated();

    /// <summary>
    ///     Configures the background task queue. Order-insensitive: the delegate is stored and applied during the build.
    /// </summary>
    ICqrsBuilder ConfigureQueue(Action<BackgroundTaskQueueOptions> configure);

    /// <summary>
    ///     Configures the notification outbox. Order-insensitive: the delegate is stored and applied during the build.
    /// </summary>
    ICqrsBuilder ConfigureOutbox(Action<OutboxOptions> configure);

    /// <summary>
    ///     Configures dispatcher options (run mode, scope mode, publish strategy). Order-insensitive: the delegate is
    ///     stored and applied during the build.
    /// </summary>
    ICqrsBuilder ConfigureDispatcher(Action<DispatcherOptions> configure);

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
    ///     Enables the idempotency behavior (at-most-once processing for idempotent requests). Order-insensitive: it
    ///     sets a flag the build consumes. The caller must still register an idempotency store.
    /// </summary>
    ICqrsBuilder UseIdempotency();

    /// <summary>
    ///     Enables and configures the unit-of-work behavior with an AOT-safe factory for the concrete unit of work.
    ///     Order-insensitive: it stores the factory and options on the pack accumulator.
    /// </summary>
    ICqrsBuilder UseUnitOfWork<TUnitOfWork>(
        Func<IServiceProvider, TUnitOfWork> factory,
        Action<UnitOfWorkOptions>? configure = null)
        where TUnitOfWork : class, IUnitOfWork;

    /// <summary>
    ///     Registers the in-process in-memory outbox store. Order-insensitive: it records the intent (and any options)
    ///     so the store is registered during the build. The store is not durable — for development, tests, and
    ///     single-node demos only.
    /// </summary>
    ICqrsBuilder UseInMemoryOutbox(Action<InMemoryOutboxStoreOptions>? configure = null);
}
