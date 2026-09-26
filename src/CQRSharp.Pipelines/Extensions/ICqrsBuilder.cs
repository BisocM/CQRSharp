using CQRSharp.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Pipelines;

/// <summary>
///     The fluent builder for wiring CQRSharp into a service collection, reached through
///     <c>AddCqrsGenerated(Action&lt;ICqrsBuilder&gt;)</c> (which also applies the source-generated registrations).
/// </summary>
/// <remarks>
///     <para>
///         Every verb only <em>records intent</em>; the recorded configuration is applied in one fixed sequence when the
///         builder is built, so the order in which different verbs are called never changes the result. Repeating a verb
///         adds to it: configuration delegates run in call order (a later assignment to the same property wins), and a
///         switch, a policy, a clock or a unit-of-work factory takes the value of the last call.
///     </para>
///     <para>
///         The validation and exception-handling behaviors are registered unless <see cref="UseValidation" /> or
///         <see cref="UseExceptionHandling" /> turns them off, by the parameterless <c>AddCqrsGenerated()</c> (the builder
///         with nothing configured) too; they do nothing for a request with no validators or exception hooks. Every other
///         behavior is registered only by its own verb.
///     </para>
///     <para>
///         Several <c>AddCqrsGenerated(builder)</c> calls on one service collection (a library's own wiring and its
///         host's) add up: each call's verbs take effect, options both calls set apply in call order, and a behavior one
///         call registered stays registered — an opt-out such as <c>UseValidation(false)</c> applies to its own call only
///         and cannot remove what another call registered.
///     </para>
/// </remarks>
public interface ICqrsBuilder
{
    /// <summary>
    ///     The underlying service collection, for registrations the builder does not model (your handlers'
    ///     dependencies, custom behaviors, alternative stores). Registrations made here are applied immediately, so unlike
    ///     the builder's verbs their effect can depend on their order relative to other <c>Services</c> calls.
    /// </summary>
    IServiceCollection Services { get; }

    /// <summary>
    ///     Configures the background task queue. Repeated calls compose: each delegate runs, in call order.
    /// </summary>
    /// <param name="configure">Sets the queue options.</param>
    /// <returns>This builder, for chaining.</returns>
    ICqrsBuilder ConfigureQueue(Action<BackgroundTaskQueueOptions> configure);

    /// <summary>
    ///     Configures dispatcher options (run mode and scope mode). Repeated calls compose: each delegate runs, in call
    ///     order.
    /// </summary>
    /// <param name="configure">Sets the dispatcher options.</param>
    /// <returns>This builder, for chaining.</returns>
    ICqrsBuilder ConfigureDispatcher(Action<DispatcherOptions> configure);

    /// <summary>
    ///     Configures notification dispatch options (the publish strategy). Repeated calls compose: each delegate runs, in
    ///     call order.
    /// </summary>
    /// <param name="configure">Sets the notification options.</param>
    /// <returns>This builder, for chaining.</returns>
    ICqrsBuilder ConfigureNotifications(Action<NotificationOptions> configure);

    /// <summary>
    ///     Enables (or disables) the fail-fast startup validator in every environment: enabled maps to <c>ThrowOnError</c>
    ///     (abort host start on a configuration error), disabled to <c>Off</c>. For <c>WarnOnly</c> or
    ///     <c>ThrowOnWarning</c>, use the overload that takes a <see cref="CqrsValidationPolicy" />. The last call wins. A
    ///     builder that never calls it leaves the policy as it is: set elsewhere (another <c>AddCqrsGenerated</c> call, a
    ///     <c>Configure&lt;CqrsStartupValidationOptions&gt;</c>, a configuration binding), or else unset, which is
    ///     <c>ThrowOnError</c> when the host environment is Development and <c>Off</c> otherwise.
    /// </summary>
    /// <param name="enabled"><see langword="true" /> for <c>ThrowOnError</c>; <see langword="false" /> for <c>Off</c>.</param>
    /// <returns>This builder, for chaining.</returns>
    ICqrsBuilder ValidateOnStart(bool enabled = true);

    /// <summary>
    ///     Sets the fail-fast startup validator's policy explicitly (<c>Off</c>, <c>WarnOnly</c>, <c>ThrowOnError</c>, or
    ///     <c>ThrowOnWarning</c>), in every environment. The last call wins; see <see cref="ValidateOnStart(bool)" />.
    /// </summary>
    /// <param name="policy">The policy the startup validator applies.</param>
    /// <returns>This builder, for chaining.</returns>
    ICqrsBuilder ValidateOnStart(CqrsValidationPolicy policy);

    /// <summary>
    ///     Sets the authoritative <see cref="TimeProvider" /> (the clock every time-dependent component reads). The build
    ///     removes any earlier registration and registers this one, and the last call wins.
    /// </summary>
    /// <param name="timeProvider">The clock.</param>
    /// <returns>This builder, for chaining.</returns>
    ICqrsBuilder UseTimeProvider(TimeProvider timeProvider);

    /// <summary>
    ///     Sets the authoritative <see cref="TimeProvider" /> via a factory resolved from the service provider, exactly as
    ///     <see cref="UseTimeProvider(TimeProvider)" />. The factory must return a concrete provider — resolve a
    ///     <em>distinct</em> clock type or return <see cref="TimeProvider.System" />. It must not resolve
    ///     <see cref="TimeProvider" /> itself (<c>sp.GetService&lt;TimeProvider&gt;()</c>): that factory is this very
    ///     registration, so resolving it would recurse (it throws a clear error instead). To defer to a
    ///     <see cref="TimeProvider" /> your host already registered, don't call this at all: <c>AddCqrsGenerated</c>
    ///     registers <see cref="TimeProvider.System" /> with TryAdd, so an existing registration wins.
    /// </summary>
    /// <param name="factory">Builds the clock from the service provider.</param>
    /// <returns>This builder, for chaining.</returns>
    ICqrsBuilder UseTimeProvider(Func<IServiceProvider, TimeProvider> factory);

    /// <summary>
    ///     Enables the logging behavior, which logs the start, completion (with elapsed time) and failure of each request
    ///     and stream.
    /// </summary>
    /// <returns>This builder, for chaining.</returns>
    ICqrsBuilder UseLogging();

    /// <summary>
    ///     Keeps (the default) or turns off the validation behavior, which runs every registered
    ///     <c>IRequestValidator&lt;TRequest&gt;</c> before the handler and rejects the request with a
    ///     <see cref="RequestValidationException" /> when any reports a failure. It is registered unless this is called with
    ///     <see langword="false" />; the last call wins.
    /// </summary>
    /// <param name="enabled"><see langword="false" /> to leave the validation behavior out.</param>
    /// <returns>This builder, for chaining.</returns>
    ICqrsBuilder UseValidation(bool enabled = true);

    /// <summary>
    ///     Keeps (the default) or turns off the exception-handling behavior, which runs the request's exception actions and
    ///     handlers when the request fails with an exception. It is registered unless this is called with
    ///     <see langword="false" />; the last call wins.
    /// </summary>
    /// <param name="enabled"><see langword="false" /> to leave the exception-handling behavior out.</param>
    /// <returns>This builder, for chaining.</returns>
    ICqrsBuilder UseExceptionHandling(bool enabled = true);

    /// <summary>
    ///     Enables and configures the rate-limiting behavior, which applies to requests whose context implements
    ///     <see cref="IRateLimitedContext" />. Repeated calls compose: each delegate runs, in call order.
    /// </summary>
    /// <param name="configure">Sets the rate-limiting options.</param>
    /// <returns>This builder, for chaining.</returns>
    ICqrsBuilder UseRateLimiting(Action<RateLimitingOptions> configure);

    /// <summary>
    ///     Enables and configures the resilience behavior, which retries requests implementing
    ///     <c>IRetryableRequest</c>. Repeated calls compose: each delegate runs, in call order.
    /// </summary>
    /// <param name="configure">Sets the retry options.</param>
    /// <returns>This builder, for chaining.</returns>
    ICqrsBuilder UseResilience(Action<ResilienceOptions> configure);

    /// <summary>
    ///     Enables and configures the timeout behavior, which cancels the token a request's handler receives once the
    ///     request runs past its timeout and, when the handler observes the cancellation, fails the request with a
    ///     <see cref="RequestTimeoutException" />. Repeated calls
    ///     compose: each delegate runs, in call order.
    /// </summary>
    /// <param name="configure">Sets the timeout options.</param>
    /// <returns>This builder, for chaining.</returns>
    ICqrsBuilder UseTimeout(Action<TimeoutOptions> configure);

    /// <summary>
    ///     Enables the notification outbox in one step: selects the mode (<c>Enabled</c> by default) and registers the
    ///     store chosen via <see cref="OutboxStoreBuilder" />, which replaces any outbox store already registered. With no
    ///     store chosen, the in-memory store is used only when no outbox store is registered at all, so a store registered
    ///     explicitly (<c>AddRedisOutboxStore</c>, ...) wins in either order. Repeated calls configure the same
    ///     <see cref="OutboxStoreBuilder" />, so a later call that chooses no store keeps an earlier call's choice. Not
    ///     calling this leaves the outbox off.
    /// </summary>
    /// <param name="configure">Chooses the mode, the store and the processor options.</param>
    /// <returns>This builder, for chaining.</returns>
    ICqrsBuilder UseOutbox(Action<OutboxStoreBuilder> configure);

    /// <summary>
    ///     Enables idempotency (at-most-once processing for requests implementing <c>IIdempotentRequest</c>) in one step:
    ///     turns on the behavior and registers the store. A store chosen via the <see cref="IdempotencyStoreBuilder" />
    ///     (e.g. <c>i => i.UseRedis(...)</c>) replaces any idempotency store already registered; with none chosen, the
    ///     in-memory store is used only when no idempotency store is registered at all. Repeated calls configure the same
    ///     <see cref="IdempotencyStoreBuilder" />, so a later call that chooses no store keeps an earlier call's choice.
    /// </summary>
    /// <param name="configure">Chooses the store and, optionally, the result serializer; <see langword="null" /> keeps the defaults.</param>
    /// <returns>This builder, for chaining.</returns>
    ICqrsBuilder UseIdempotency(Action<IdempotencyStoreBuilder>? configure = null);

    /// <summary>
    ///     Enables and configures the unit-of-work behavior, which runs requests marked <see cref="ITransactionalCommand" />
    ///     or <see cref="ITransactionalQuery" /> in a transaction of the <see cref="IUnitOfWork" /> the factory builds. The
    ///     last factory wins; <paramref name="configure" /> delegates compose, in call order.
    /// </summary>
    /// <typeparam name="TUnitOfWork">The concrete unit of work, registered scoped as <see cref="IUnitOfWork" />.</typeparam>
    /// <param name="factory">Builds the unit of work from the request scope.</param>
    /// <param name="configure">Sets the unit-of-work options.</param>
    /// <returns>This builder, for chaining.</returns>
    ICqrsBuilder UseUnitOfWork<TUnitOfWork>(
        Func<IServiceProvider, TUnitOfWork> factory,
        Action<UnitOfWorkOptions>? configure = null)
        where TUnitOfWork : class, IUnitOfWork;
}
