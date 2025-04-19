using System.Threading.Channels;
using CQRSharp.Core.BackgroundTasks.Types;

namespace CQRSharp.Core.BackgroundTasks
{
    /// <summary>
    /// Defines a background‑task queue with detailed introspection and metrics.
    /// Notifications are now published via INotificationDispatcher.
    /// </summary>
    public interface IBackgroundTaskQueue
    {
        /// <summary>
        /// Queues a background work item to be processed asynchronously.
        /// </summary>
        Task<QueueWriteResult> QueueBackgroundWorkItemAsync(
            Func<CancellationToken, Task> workItem,
            CancellationToken cancellationToken);

        /// <summary>
        /// Dequeues the next background work item, waiting asynchronously if none are available.
        /// </summary>
        ValueTask<QueuedTask> DequeueAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Provides access to the channel reader for high‑throughput batch dequeue patterns.
        /// </summary>
        ChannelReader<QueuedTask> Reader { get; }

        /// <summary>
        /// Total number of items ever enqueued (including those later dropped).
        /// </summary>
        long TotalItemsEnqueued { get; }

        /// <summary>
        /// Total number of items dropped under DropNewest or DropWrite policies.
        /// </summary>
        long TotalDroppedNewest { get; }

        /// <summary>
        /// Total number of items dropped under the DropOldest policy.
        /// </summary>
        long TotalDroppedOldest { get; }
    }
}