using System.Threading.Channels;
using CQRSharp.Core.BackgroundTasks.Types;

namespace CQRSharp.Core.Options
{
    /// <summary>
    /// Configuration options for <see cref="CQRSharp.Core.BackgroundTasks.BackgroundTaskQueue"/>.
    /// </summary>
    public sealed class BackgroundTaskQueueOptions
    {
        /// <summary>
        /// Gets or sets the maximum number of items the queue may hold.
        /// A value less than or equal to zero indicates an unbounded queue.
        /// </summary>
        public int Capacity { get; set; }

        /// <summary>
        /// Gets or sets the policy to apply when <see cref="Capacity"/> is reached.
        /// Defaults to <see cref="BoundedChannelFullMode.Wait"/>.
        /// </summary>
        public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.Wait;

        /// <summary>
        /// Gets or sets the number of consumer instances to spawn.
        /// A value less than or equal to zero defaults to <c>Environment.ProcessorCount</c>.
        /// </summary>
        public int ConsumerCount { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of work items to dequeue in one batch.
        /// A value less than or equal to zero drains the queue until empty on each wake‑up.
        /// </summary>
        public int DequeueBatchSize { get; set; }

        /// <summary>
        /// Gets or sets an optional callback invoked when a work item is rejected due to backpressure policy.
        /// </summary>
        public Action<TaskRejectedEventArgs>? OnTaskRejected { get; set; }

        /// <summary>
        /// Gets or sets an optional callback invoked when a work item is successfully enqueued.
        /// </summary>
        public Action<TaskEnqueuedEventArgs>? OnTaskEnqueued { get; set; }

        /// <summary>
        /// Gets or sets the number of shards to use for the internal queue channels.
        /// A value &lt;= 0 defaults to:
        /// - <c>Math.Min(Capacity, Environment.ProcessorCount)</c> for bounded queues,
        /// - <c>Environment.ProcessorCount</c> for unbounded queues.
        /// </summary>
        public int ShardCount { get; set; }
    }
}