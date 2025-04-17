using System.Threading.Channels;

namespace CQRSharp.Core.Options
{
    /// <summary>
    /// Configuration options for <see cref="CQRSharp.Core.BackgroundTasks.BackgroundTaskQueue"/>.
    /// </summary>
    public sealed class BackgroundTaskQueueOptions
    {
        /// <summary>
        /// The maximum number of items the queue may hold.
        /// If set to a value ≤ 0, the queue is unbounded.
        /// </summary>
        public int Capacity { get; set; }

        /// <summary>
        /// Policy to apply when <see cref="Capacity"/> is reached.
        /// Default is <see cref="BoundedChannelFullMode.Wait"/>.
        /// </summary>
        public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.Wait;

        /// <summary>
        /// Number of consumer instances to spawn.
        /// If ≤ 0, defaults to <c>Environment.ProcessorCount</c>.
        /// </summary>
        public int ConsumerCount { get; set; }

        /// <summary>
        /// Maximum number of work items to dequeue in one batch.
        /// If ≤ 0, drains until the channel is empty each wake‑up.
        /// </summary>
        public int DequeueBatchSize { get; set; }

        /// <summary>
        /// Callback invoked when a work item is rejected due to backpressure policy.
        /// </summary>
        public Action<Func<CancellationToken, Task>>? OnTaskRejected { get; set; }
    }
}