namespace CQRSharp.Core.BackgroundTasks;

public interface IBackgroundTaskQueue
{
    /// <summary>
    ///     Queues a background work item to be processed asynchronously. The work item is represented as a delegate
    ///     that accepts a <see cref="CancellationToken" /> and returns a <see cref="Task" />.
    /// </summary>
    /// <param name="workItem">
    ///     The background work item to be queued. It is a function that takes a <see cref="CancellationToken" /> and
    ///     returns a <see cref="Task" /> representing the asynchronous operation.
    /// </param>
    /// <param name="ct">
    ///     A <see cref="CancellationToken" /> that can be used to cancel the operation of queuing the work item.
    /// </param>
    /// <returns>
    ///     A <see cref="Task" /> that completes when the work item has been successfully queued.
    /// </returns>
    public Task QueueBackgroundWorkItemAsync(Func<CancellationToken, Task> workItem, CancellationToken ct);

    /// <summary>
    ///     Dequeues a background work item from the queue to be processed asynchronously.
    ///     The dequeued item is a delegate that accepts a <see cref="CancellationToken" />
    ///     and returns a <see cref="Task" />.
    /// </summary>
    /// <param name="cancellationToken">
    ///     A <see cref="CancellationToken" /> used to cancel the dequeue operation.
    /// </param>
    /// <returns>
    ///     A <see cref="Task" /> that completes with a function representing the dequeued
    ///     work item. The function takes a <see cref="CancellationToken" /> and returns
    ///     a <see cref="Task" /> for the asynchronous operation.
    /// </returns>
    public Task<Func<CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken);
}