namespace CQRSharp.Pipelines;

/// <summary>
///     The execution priorities of the built-in pipeline behaviors. Lower values run earlier, further from the handler,
///     so the built-ins wrap each other in this order: exception handling, rate limiting, logging, validation,
///     resilience, idempotency, unit of work, timeout. A custom behavior that implements no priority runs at
///     <see cref="IPrioritizedPipelineBehavior.DefaultPriority" />, inside all of them; reference these constants to
///     place one elsewhere.
/// </summary>
public static class CqrsPipelinePriorities
{
    /// <summary>The exception-handling behavior: the outermost of all, so every hook sees every failure.</summary>
    public const int ExceptionHandling = int.MinValue;

    /// <summary>
    ///     The rate-limiting behavior: just inside exception handling and ahead of everything else, so a throttled
    ///     request is rejected before any other behavior or the handler does work (it is also not logged by the logging
    ///     behavior).
    /// </summary>
    public const int RateLimiting = int.MinValue + 1;

    /// <summary>
    ///     The logging behavior: inside exception handling and rate limiting, and outside everything that does work, so
    ///     its elapsed time covers validation, retries, the unit of work and the handler.
    /// </summary>
    public const int Logging = -100;

    /// <summary>The validation behaviors: inside logging, outside everything that does work.</summary>
    public const int Validation = -50;

    /// <summary>
    ///     The resilience behavior: outside idempotency and the unit of work, so each retry begins after the failed
    ///     attempt's transaction was rolled back and its idempotency claim released.
    /// </summary>
    public const int Resilience = 50;

    /// <summary>
    ///     The idempotency behavior: inside resilience (so a released claim can be retried) but outside the unit of work,
    ///     so a duplicate request short-circuits before any transaction is opened.
    /// </summary>
    public const int Idempotency = 75;

    /// <summary>The unit-of-work behavior: inside idempotency, around the timeout and the handler.</summary>
    public const int UnitOfWork = 100;

    /// <summary>
    ///     The timeout behavior: the innermost built-in, inside the unit of work. It bounds the handler and any custom
    ///     behavior at the default priority, not the built-ins around it.
    /// </summary>
    public const int Timeout = 300;
}
