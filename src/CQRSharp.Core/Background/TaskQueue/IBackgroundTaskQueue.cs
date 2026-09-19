using CQRSharp.Core.Background.TaskQueue.Types;

namespace CQRSharp.Core.Background.TaskQueue;

/// <summary>
///     Defines the internal contract for the background task queue,
///     exposing the necessary components for the consumer service.
/// </summary>
/// <remarks>
///     This interface should remain internal to the Core project. It is not intended
///     for public use. Its purpose is to decouple the queue's implementation from its consumer,
///     allowing the consumer to access only what it needs to function.
///     The public-facing contract for enqueuing work is <see cref="IBackgroundTaskManager" />.
/// </remarks>
internal interface IBackgroundTaskQueue
{
    /// <summary>
    ///     Dequeues the next work item, waiting asynchronously until one is available.
    /// </summary>
    internal ValueTask<QueuedTask> DequeueAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Stops accepting new work. What is already queued can still be dequeued; once it is gone,
    ///     <see cref="DequeueAsync" /> throws <see cref="System.Threading.Channels.ChannelClosedException" />.
    /// </summary>
    internal void CompleteAdding();

    /// <summary>Cancels every work item still queued, so nothing awaiting one hangs. Returns how many there were.</summary>
    internal int CancelPending();
}