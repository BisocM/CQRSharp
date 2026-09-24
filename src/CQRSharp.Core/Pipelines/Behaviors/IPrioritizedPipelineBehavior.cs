namespace CQRSharp.Pipelines;

/// <summary>
///     Optional contract for a pipeline behavior that needs a place in the order: lower values run earlier, further from
///     the handler. The built-in behaviors' values are in <c>CqrsPipelinePriorities</c>.
/// </summary>
public interface IPrioritizedPipelineBehavior
{
    /// <summary>
    ///     The priority assigned to behaviors that do not implement <see cref="IPrioritizedPipelineBehavior" />.
    ///     They run inside every built-in behavior (and among themselves in order of type name).
    /// </summary>
    public const int DefaultPriority = int.MaxValue / 2;

    /// <summary>
    ///     Execution priority for this behavior. Lower runs earlier.
    /// </summary>
    int PipelineExecutionPriority { get; }
}