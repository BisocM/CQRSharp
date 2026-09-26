namespace CQRSharp.Core.BackgroundTasks;

/// <summary>
///     The consumer's side of the background task queue. The public side, for enqueueing work, is
///     <see cref="IBackgroundTaskManager" />.
/// </summary>
internal interface IBackgroundTaskQueue
{
    /// <summary>
    ///     Waits for the next work item and claims it for execution: the caller must run it. Items whose callers gave up
    ///     while they waited are skipped. Returns <see langword="null" /> once the queue is completed and empty. Once
    ///     <paramref name="cancellationToken" /> has fired it takes nothing, even when an item is ready.
    /// </summary>
    ValueTask<QueuedItem?> DequeueAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Stops accepting new work, refusing producers that are waiting for room as well. What is already queued can
    ///     still be dequeued.
    /// </summary>
    void CompleteAdding();

    /// <summary>
    ///     Cancels every work item still queued, so nothing awaiting one hangs. Returns how many there were. Call it after
    ///     <see cref="CompleteAdding" />, or new work could arrive behind it.
    /// </summary>
    int CancelPending();
}
