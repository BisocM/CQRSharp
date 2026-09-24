namespace CQRSharp.Core.BackgroundTasks;

/// <summary>
///     Runs work on the background task queue: the bounded queue, concurrency limit and shutdown drain that
///     <see cref="RunMode.Queued" /> dispatch uses, configured by <see cref="BackgroundTaskQueueOptions" />. The work runs
///     once the host's queue consumer reaches it (so the Generic Host must be running), and the returned task reports
///     its outcome.
/// </summary>
public interface IBackgroundTaskManager
{
    /// <summary>Queues a work item that produces no result.</summary>
    /// <param name="workItem">
    ///     The work. Its token fires when the host stops and the shutdown budget
    ///     (<see cref="BackgroundTaskQueueOptions.ShutdownTimeout" />) runs out while the work is still running.
    /// </param>
    /// <param name="cancellationToken">
    ///     Gives up on the work item while it has not started: it ends a wait for room in a full queue
    ///     (<see cref="System.Threading.Channels.BoundedChannelFullMode.Wait" />) and withdraws an item that is still
    ///     queued, so the returned task is cancelled at once. It does not reach work that has already started.
    /// </param>
    /// <returns>
    ///     A task that completes when the work item has run, faulted or cancelled as the work was. It is cancelled when
    ///     <paramref name="cancellationToken" /> withdraws the item or shutdown cancels it before it started, and faults
    ///     with <see cref="BackgroundTaskRejectedException" /> when the queue refuses or evicts it.
    /// </returns>
    Task EnqueueAsync(Func<CancellationToken, Task> workItem, CancellationToken cancellationToken = default);

    /// <summary>Queues a work item that produces a result.</summary>
    /// <typeparam name="TResult">The type of the result the work item returns.</typeparam>
    /// <param name="workItem">
    ///     The work. Its token fires when the host stops and the shutdown budget
    ///     (<see cref="BackgroundTaskQueueOptions.ShutdownTimeout" />) runs out while the work is still running.
    /// </param>
    /// <param name="cancellationToken">
    ///     Gives up on the work item while it has not started: it ends a wait for room in a full queue
    ///     (<see cref="System.Threading.Channels.BoundedChannelFullMode.Wait" />) and withdraws an item that is still
    ///     queued, so the returned task is cancelled at once. It does not reach work that has already started.
    /// </param>
    /// <returns>
    ///     A task that completes with the work item's result, or faulted or cancelled as the work was. It is cancelled
    ///     when <paramref name="cancellationToken" /> withdraws the item or shutdown cancels it before it started, and
    ///     faults with <see cref="BackgroundTaskRejectedException" /> when the queue refuses or evicts it.
    /// </returns>
    Task<TResult> EnqueueAsync<TResult>(Func<CancellationToken, Task<TResult>> workItem, CancellationToken cancellationToken = default);
}
