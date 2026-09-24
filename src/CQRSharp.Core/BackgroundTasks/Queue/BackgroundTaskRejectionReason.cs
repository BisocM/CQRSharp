namespace CQRSharp;

/// <summary>Why the background task queue did not run a work item (<see cref="BackgroundTaskRejectedException.Reason" />).</summary>
public enum BackgroundTaskRejectionReason
{
    /// <summary>
    ///     The queue was full and its <see cref="BackgroundTaskQueueOptions.FullMode" /> is
    ///     <see cref="System.Threading.Channels.BoundedChannelFullMode.DropWrite" />, so it refused the new work item.
    /// </summary>
    QueueFull,

    /// <summary>
    ///     The queue accepted the work item, then evicted it from a full queue to make room for newer work: its
    ///     <see cref="BackgroundTaskQueueOptions.FullMode" /> is
    ///     <see cref="System.Threading.Channels.BoundedChannelFullMode.DropOldest" /> or
    ///     <see cref="System.Threading.Channels.BoundedChannelFullMode.DropNewest" />.
    /// </summary>
    Evicted,

    /// <summary>The queue no longer accepts work: the host is shutting down, or the queue was disposed.</summary>
    QueueClosed
}
