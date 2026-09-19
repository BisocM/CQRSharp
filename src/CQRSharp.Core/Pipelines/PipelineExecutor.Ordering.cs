using CQRSharp.Pipelines;

namespace CQRSharp.Core.Pipelines;

public sealed partial class PipelineExecutor
{
    private const int DefaultBehaviorPriority = IPrioritizedPipelineBehavior.DefaultPriority;

    /// <summary>
    ///     Shared total-ordering tie-break used by the pipeline/handler comparers: order by priority, then by type
    ///     full name so the order is deterministic for equal priorities.
    /// </summary>
    private static int CompareByPriorityThenName(int leftPriority, int rightPriority, object left, object right)
    {
        var byPriority = leftPriority.CompareTo(rightPriority);
        return byPriority != 0
            ? byPriority
            : string.CompareOrdinal(left.GetType().FullName, right.GetType().FullName);
    }

    private sealed class PreHandlerPriorityComparer : IComparer<IPreHandlerAttribute>
    {
        private PreHandlerPriorityComparer()
        {
        }

        public static PreHandlerPriorityComparer Instance { get; } = new();

        public int Compare(IPreHandlerAttribute? x, IPreHandlerAttribute? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            return CompareByPriorityThenName(x.PreHandlerExecutionPriority, y.PreHandlerExecutionPriority, x, y);
        }
    }

    private sealed class PostHandlerPriorityComparer : IComparer<IPostHandlerAttribute>
    {
        private PostHandlerPriorityComparer()
        {
        }

        public static PostHandlerPriorityComparer Instance { get; } = new();

        public int Compare(IPostHandlerAttribute? x, IPostHandlerAttribute? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            return CompareByPriorityThenName(x.PostHandlerExecutionPriority, y.PostHandlerExecutionPriority, x, y);
        }
    }

    private sealed class BehaviorPriorityComparer<TRequest, TResult> : IComparer<IPipelineBehavior<TRequest, TResult>> where TRequest : IRequest
    {
        private BehaviorPriorityComparer()
        {
        }

        public static BehaviorPriorityComparer<TRequest, TResult> Instance { get; } = new();

        public int Compare(IPipelineBehavior<TRequest, TResult>? x, IPipelineBehavior<TRequest, TResult>? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var left = x is IPrioritizedPipelineBehavior lp ? lp.PipelineExecutionPriority : DefaultBehaviorPriority;
            var right = y is IPrioritizedPipelineBehavior rp ? rp.PipelineExecutionPriority : DefaultBehaviorPriority;
            return CompareByPriorityThenName(left, right, x, y);
        }
    }

    private sealed class StreamBehaviorPriorityComparer<TRequest, TItem> : IComparer<IStreamPipelineBehavior<TRequest, TItem>>
        where TRequest : IRequest
    {
        private StreamBehaviorPriorityComparer()
        {
        }

        public static StreamBehaviorPriorityComparer<TRequest, TItem> Instance { get; } = new();

        public int Compare(IStreamPipelineBehavior<TRequest, TItem>? x, IStreamPipelineBehavior<TRequest, TItem>? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var left = x is IPrioritizedPipelineBehavior lp ? lp.PipelineExecutionPriority : DefaultBehaviorPriority;
            var right = y is IPrioritizedPipelineBehavior rp ? rp.PipelineExecutionPriority : DefaultBehaviorPriority;
            return CompareByPriorityThenName(left, right, x, y);
        }
    }
}
