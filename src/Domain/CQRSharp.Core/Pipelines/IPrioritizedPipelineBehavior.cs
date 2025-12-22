namespace CQRSharp.Core.Pipelines;

/// <summary>
///     Optional contract for pipeline behaviors that require a deterministic execution order.
///     Lower values execute earlier.
/// </summary>
public interface IPrioritizedPipelineBehavior
{
    /// <summary>
    ///     Execution priority for this behavior. Lower runs earlier.
    /// </summary>
    int PipelineExecutionPriority { get; }
}

