using System.Threading.Channels;
using CQRSharp.Core.BackgroundTasks.Types;

namespace CQRSharp.Core.BackgroundTasks;

/// <summary>
///     Defines a background‑task queue with detailed introspection and metrics.
///     Notifications are now published via INotificationDispatcher.
/// </summary>
public interface IBackgroundTaskQueue
{
    /// <summary>
    ///     Provides access to the channel reader for high‑throughput batch dequeue patterns.
    /// </summary>
    internal ChannelReader<QueuedTask> Reader { get; }

    /// <summary>
    ///     Queues a background work item to be processed asynchronously.
    /// </summary>
    internal Task<QueueWriteResult> QueueBackgroundWorkItemAsync(
        Func<CancellationToken, Task> workItem,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Dequeues the next background work item, waiting asynchronously if none are available.
    /// </summary>
    internal ValueTask<QueuedTask> DequeueAsync(CancellationToken cancellationToken);
}