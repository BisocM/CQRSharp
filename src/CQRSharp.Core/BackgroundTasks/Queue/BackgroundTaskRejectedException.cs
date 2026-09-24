namespace CQRSharp;

/// <summary>
///     Thrown to the caller of a background work item that the background task queue will not run: the queue was full
///     and refuses new work (<see cref="System.Threading.Channels.BoundedChannelFullMode.DropWrite" />), it evicted the
///     item to make room for newer work (<see cref="System.Threading.Channels.BoundedChannelFullMode.DropOldest" /> /
///     <see cref="System.Threading.Channels.BoundedChannelFullMode.DropNewest" />), or it no longer accepts work because
///     the host is shutting down. The work never started. Under <see cref="RunMode.Queued" /> this is what awaiting the
///     dispatch throws; an HTTP API would typically answer 503.
/// </summary>
public sealed class BackgroundTaskRejectedException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="reason">Why the queue did not run the work item.</param>
    /// <param name="message">A message that explains the rejection.</param>
    public BackgroundTaskRejectedException(BackgroundTaskRejectionReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Why the queue did not run the work item.</summary>
    public BackgroundTaskRejectionReason Reason { get; }
}
