namespace CQRSharp.Core.BackgroundTasks.Types;

/// <summary>
/// Provides detailed information when a work item is successfully enqueued.
/// </summary>
public readonly struct TaskEnqueuedEventArgs
{
    /// <summary>
    /// Gets the sequence number assigned to the enqueued work item.
    /// </summary>
    public long SequenceNumber { get; }

    /// <summary>
    /// Gets the delegate representing the enqueued work item.
    /// </summary>
    public Func<CancellationToken, Task> WorkItem { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskEnqueuedEventArgs"/> struct.
    /// </summary>
    /// <param name="sequenceNumber">The sequence number of the enqueued work item.</param>
    /// <param name="workItem">The work item delegate that was enqueued.</param>
    public TaskEnqueuedEventArgs(long sequenceNumber, Func<CancellationToken, Task> workItem)
    {
        SequenceNumber = sequenceNumber;
        WorkItem = workItem;
    }
}