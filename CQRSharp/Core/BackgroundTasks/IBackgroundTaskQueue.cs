using System.Threading.Channels;

namespace CQRSharp.Core.BackgroundTasks
{
    /// <summary>
    /// Defines a contract for a background task queue capable of scheduling
    /// and retrieving work items, and exposing the underlying channel reader
    /// for high‑throughput batch dequeue scenarios.
    /// </summary>
    public interface IBackgroundTaskQueue
    {
        /// <summary>
        /// Queues a background work item to be processed asynchronously.
        /// </summary>
        /// <param name="workItem">
        /// Delegate representing the work to be processed. Must not be null.
        /// </param>
        /// <param name="cancellationToken">
        /// Token to cancel the enqueue operation.
        /// </param>
        /// <returns>
        /// A <see cref="Task"/> representing the asynchronous enqueue operation.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown if <paramref name="workItem"/> is null.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown if the queue is full and the configured policy is to throw.
        /// </exception>
        Task QueueBackgroundWorkItemAsync(
            Func<CancellationToken, Task> workItem,
            CancellationToken cancellationToken);

        /// <summary>
        /// Dequeues the next background work item, waiting asynchronously if none are available.
        /// </summary>
        /// <param name="cancellationToken">
        /// Token to cancel the dequeue operation.
        /// </param>
        /// <returns>
        /// A <see cref="ValueTask{TResult}"/> whose result is the dequeued work delegate.
        /// </returns>
        ValueTask<Func<CancellationToken, Task>> DequeueAsync(
            CancellationToken cancellationToken);

        /// <summary>
        /// Provides direct access to the channel reader for batch dequeue patterns.
        /// </summary>
        ChannelReader<Func<CancellationToken, Task>> Reader { get; }
    }
}