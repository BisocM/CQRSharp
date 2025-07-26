namespace CQRSharp.Core.Background.TaskQueue;

/// <summary>
/// Defines a public contract for queuing background work items.
/// This interface provides a simplified, Task-based API for consumers,
/// allowing them to await the completion and result of background tasks.
/// </summary>
public interface IBackgroundTaskManager
{
    /// <summary>
    /// Queues a work item to be executed in the background.
    /// This method is for "fire and forget" style tasks where no result is expected.
    /// </summary>
    /// <param name="workItem">A function that represents the asynchronous work item to be executed.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the enqueuing operation if the queue is full and configured to wait.</param>
    /// <returns>
    /// A <see cref="Task"/> that completes when the work item has finished executing.
    /// The returned task will transition to a faulted state if the work item throws an exception or a cancelled state if it is cancelled.
    /// </returns>
    Task EnqueueAsync(Func<CancellationToken, Task> workItem, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a work item with a result to be executed in the background.
    /// </summary>
    /// <typeparam name="TResult">The type of the result returned by the work item.</typeparam>
    /// <param name="workItem">A function that represents the asynchronous work item to be executed, which returns a result.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the enqueuing operation if the queue is full and configured to wait.</param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> that completes with the result of the work item.
    /// The returned task will transition to a faulted state if the work item throws an exception or a cancelled state if it is cancelled.
    /// </returns>
    Task<TResult> EnqueueAsync<TResult>(Func<CancellationToken, Task<TResult>> workItem, CancellationToken cancellationToken = default);
}