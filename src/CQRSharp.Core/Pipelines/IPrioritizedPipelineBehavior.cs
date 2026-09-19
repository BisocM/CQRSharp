namespace CQRSharp.Core.Pipelines;

/// <summary>
///     Optional contract for pipeline behaviors that require a deterministic execution order.
///     Lower values execute earlier.
/// </summary>
public interface IPrioritizedPipelineBehavior
{
    /// <summary>
    ///     The priority assigned to behaviors that do not implement <see cref="IPrioritizedPipelineBehavior" />.
    ///     They sort after explicitly-prioritized behaviors (and among themselves by type name).
    /// </summary>
    public const int DefaultPriority = int.MaxValue / 2;

    /// <summary>
    ///     Execution priority for this behavior. Lower runs earlier.
    /// </summary>
    int PipelineExecutionPriority { get; }
}