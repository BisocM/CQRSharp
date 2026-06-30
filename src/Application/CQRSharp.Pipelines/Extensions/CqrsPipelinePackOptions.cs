using CQRSharp.Abstractions.Interfaces.Transactions;
using CQRSharp.Pipelines.Options;

namespace CQRSharp.Pipelines.Extensions;

/// <summary>
///     Configures which optional pipeline behaviors <c>AddCqrsPipelinePack</c> registers and how it configures the
///     ones that need options. The boolean <c>Include*</c> flags toggle parameterless behaviors; the <c>Configure*</c>
///     delegates both opt a behavior in (a null delegate means "do not register it") and supply its settings.
/// </summary>
public sealed class CqrsPipelinePackOptions
{
    /// <summary>
    ///     Registers request-level exception-hook support (the exception-handling behavior). On by default <em>whenever
    ///     the pipeline pack is active</em> (i.e. whenever any pack verb is used): catching and routing handler
    ///     exceptions through registered hooks is a cross-cutting concern almost every app wants, and the behavior is
    ///     inert (adds no overhead and changes no result) when no exception is thrown and no hooks are registered, so
    ///     enabling it by default is safe. Opt out with the builder's <c>UseExceptionHandling(false)</c>.
    /// </summary>
    public bool IncludeExceptionHandling { get; set; } = true;

    /// <summary>
    ///     Registers the validation behavior, which runs every registered <c>IRequestValidator&lt;TRequest&gt;</c> for a
    ///     request before its handler. On by default <em>whenever the pipeline pack is active</em> (i.e. whenever any
    ///     pack verb is used): rejecting invalid input at the edge is a near-universal need, and a request with no
    ///     registered validators simply passes straight through, so the default is safe. Opt out with the builder's
    ///     <c>UseValidation(false)</c>.
    /// </summary>
    public bool IncludeValidation { get; set; } = true;

    /// <summary>
    ///     Registers the logging behavior, which logs the start, completion (with elapsed time), and failure of each
    ///     request. Off by default — unlike validation and exception handling it always emits output for every request,
    ///     which would surprise consumers with unwanted log volume and would duplicate logging an app may already do.
    ///     Opt in explicitly when you want the built-in per-request logging.
    /// </summary>
    public bool IncludeLogging { get; set; } = false;

    /// <summary>
    ///     An AOT-safe factory that creates your <see cref="IUnitOfWork" /> implementation. Set this to register the
    ///     unit-of-work behavior, which wraps each request in a transaction that commits on success and rolls back on
    ///     failure. Null (the default) means no unit-of-work behavior is registered; the factory is required because
    ///     the pack cannot guess your concrete UoW type without reflection.
    /// </summary>
    public Func<IServiceProvider, IUnitOfWork>? UnitOfWorkFactory { get; set; }

    /// <summary>
    ///     Optional extra configuration for the unit-of-work behavior. Only takes effect when
    ///     <see cref="UnitOfWorkFactory" /> is also set; ignored otherwise.
    /// </summary>
    public Action<UnitOfWorkOptions>? ConfigureUnitOfWork { get; set; }

    /// <summary>
    ///     Set this to register the rate-limiting behavior and configure its limiter (token budget, replenish rate,
    ///     scope). Null (the default) means rate limiting is not registered. Note: registering the behavior only
    ///     activates it for requests whose context implements <c>IRateLimitedContext</c> — other requests pass through
    ///     untouched.
    /// </summary>
    public Action<RateLimiterOptions>? ConfigureRateLimiting { get; set; }

    /// <summary>
    ///     Set this to register the resilience behavior and configure its retry policy (max retries, base delay).
    ///     Null (the default) means no retries are added. Resilience runs outside the unit of work, so each retry runs
    ///     against a fresh transaction.
    /// </summary>
    public Action<ResilienceOptions>? ConfigureResilience { get; set; }

    /// <summary>
    ///     Set this to register the timeout behavior and configure its per-request deadline. Null (the default) means
    ///     no timeout is applied. The timeout runs closest to the handler so it bounds the handler itself rather than
    ///     the surrounding behaviors.
    /// </summary>
    public Action<TimeoutOptions>? ConfigureTimeout { get; set; }
}
