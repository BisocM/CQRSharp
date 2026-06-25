namespace CQRSharp.Pipelines;

/// <summary>
///     Well-known execution priorities for the built-in CQRSharp pipeline behaviors. Lower values run earlier
///     (further from the handler). Reference these when authoring a custom behavior that must order itself relative
///     to the built-ins.
/// </summary>
public static class CqrsPipelinePriorities
{
    /// <summary>
    ///     Resilience / retry behavior. Runs outermost (before the unit of work) so each retry executes against a
    ///     fresh transaction.
    /// </summary>
    public const int Resilience = 50;

    /// <summary>Unit-of-work / transaction behavior.</summary>
    public const int UnitOfWork = 100;

    /// <summary>
    ///     Timeout guard. Runs closest to the handler so the timeout bounds the handler rather than the outer behaviors.
    /// </summary>
    public const int Timeout = 300;
}
