using CQRSharp.Pipelines;

namespace CQRSharp.Core.Pipelines;

internal sealed partial class PipelineExecutor
{
    private const int DefaultBehaviorPriority = IPrioritizedPipelineBehavior.DefaultPriority;

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
            return PriorityOrdering.Compare(left, right, x, y);
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
            return PriorityOrdering.Compare(left, right, x, y);
        }
    }
}
