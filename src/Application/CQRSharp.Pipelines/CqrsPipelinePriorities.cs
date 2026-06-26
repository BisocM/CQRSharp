namespace CQRSharp.Pipelines;

/// <summary>
///     Well-known execution priorities for the built-in CQRSharp pipeline behaviors. Lower values run earlier
///     (further from the handler). Reference these when authoring a custom behavior that must order itself relative
///     to the built-ins.
/// </summary>
public static class CqrsPipelinePriorities
{
    /// <summary>
    ///     Rate-limiting behavior. Pinned to the minimum so it runs outermost — before any other behavior — so a
    ///     throttled request is rejected before logging, resilience, transactions, or the handler do any work.
    ///     The value is <c>int.MinValue + 1</c> (one above the absolute floor) to leave room for a future behavior
    ///     that must order itself ahead of rate limiting.
    /// </summary>
    public const int RateLimiting = int.MinValue + 1;

    /// <summary>
    ///     Logging behavior. Runs outermost so it measures and reports the full pipeline (including retries).
    /// </summary>
    public const int Logging = -100;

    /// <summary>
    ///     Resilience / retry behavior. Runs outermost (before the unit of work) so each retry executes against a
    ///     fresh transaction.
    /// </summary>
    public const int Resilience = 50;

    /// <summary>
    ///     Idempotency / duplicate-detection behavior. Runs inside resilience (so a released claim can be retried) but
    ///     before the unit of work, so a duplicate request short-circuits before any transaction is opened.
    /// </summary>
    public const int Idempotency = 75;

    /// <summary>Unit-of-work / transaction behavior.</summary>
    public const int UnitOfWork = 100;

    /// <summary>
    ///     Timeout guard. Runs closest to the handler so the timeout bounds the handler rather than the outer behaviors.
    /// </summary>
    public const int Timeout = 300;
}