namespace CQRSharp.Core.Pipeline.Attributes
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class PipelinePriorityAttribute(int priority) : Attribute
    {
        /// <summary>
        /// Default priority value for pipeline behaviors that do not specify a priority.
        /// </summary>
        public const int DefaultPriority = int.MaxValue / 2;

        /// <summary>
        /// Indicates the priority for pipeline execution. The lower the number, the higher the priority.
        /// </summary>
        public int Priority { get; } = priority;
    }
}