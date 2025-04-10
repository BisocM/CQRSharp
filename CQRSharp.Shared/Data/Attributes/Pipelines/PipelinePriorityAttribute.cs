namespace CQRSharp.Shared.Data.Attributes.Pipelines;

/// <summary>
///     An attribute used to specify the execution priority of a pipeline behavior.
/// </summary>
/// <remarks>
///     Pipeline behaviors are executed in order based on their priority values. A lower priority value indicates a higher
///     priority execution order.
///     If no priority is specified, the default priority value is <see cref="DefaultPriority" />.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PipelinePriorityAttribute(int priority) : Attribute
{
    /// <summary>
    ///     Default priority value for pipeline behaviors that do not specify a priority.
    /// </summary>
    public const int DefaultPriority = int.MaxValue / 2;

    /// <summary>
    ///     Indicates the priority for pipeline execution. The lower the number, the higher the priority.
    /// </summary>
    public int Priority { get; } = priority;
}