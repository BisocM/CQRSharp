using System.Threading.Channels;
using CQRSharp.Core.BackgroundTasks.Types;

namespace CQRSharp.Core.BackgroundTasks
{
    /// <summary>
    /// Defines a background-task queue with detailed introspection, metrics, and event hooks.
    /// </summary>
    public interface IBackgroundTaskQueue
    {
        /// <summary>
        /// Queues a background work item to be processed asynchronously.
        /// </summary>
        /// <remarks>
        /// Returns a <see cref="QueueWriteResult"/> describing whether the item was enqueued,
        /// dropped, or waited until space became available.
        /// </remarks>
        /// <param name="workItem">
        /// Delegate representing the work to be processed. Must not be null.
        /// </param>
        /// <param name="cancellationToken">
        /// Token to cancel the enqueue operation when waiting.
        /// </param>
        /// <returns>
        /// A <see cref="Task{QueueWriteResult}"/> whose result indicates the enqueue outcome and sequence number.
        /// </returns>
        Task<QueueWriteResult> QueueBackgroundWorkItemAsync(
            Func<CancellationToken, Task> workItem,
            CancellationToken cancellationToken);

        /// <summary>
        /// Dequeues the next background work item, waiting asynchronously if none are available.
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the dequeue operation.</param>
        /// <returns>
        /// A <see cref="ValueTask{TResult}"/> whose result is the dequeued <see cref="QueuedTask"/>.
        /// </returns>
        ValueTask<QueuedTask> DequeueAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Provides direct access to the channel reader for high-throughput batch dequeue patterns.
        /// </summary>
        ChannelReader<QueuedTask> Reader { get; }

        /// <summary>
        /// Gets the total number of items ever enqueued (including those later dropped).
        /// </summary>
        long TotalItemsEnqueued { get; }

        /// <summary>
        /// Gets the total number of items dropped under DropNewest or DropWrite policies.
        /// </summary>
        long TotalDroppedNewest { get; }

        /// <summary>
        /// Gets the total number of items dropped under the DropOldest policy.
        /// </summary>
        long TotalDroppedOldest { get; }

        /// <summary>
        /// Event raised whenever a work item is successfully enqueued.
        /// </summary>
        event Action<TaskEnqueuedEventArgs> OnTaskEnqueued;

        /// <summary>
        /// Event raised whenever a work item is rejected due to backpressure policy.
        /// </summary>
        event Action<TaskRejectedEventArgs> OnTaskRejected;
    }
}