namespace CQRSharp.Core.BackgroundTasks.Types;

/// <summary>
/// Wraps a background work item delegate with its assigned sequence number.
/// </summary>
public readonly struct QueuedTask
{
    /// <summary>
    /// Gets the unique sequence number of the work item.
    /// </summary>
    public long SequenceNumber { get; }

    /// <summary>
    /// Gets the delegate representing the background work item.
    /// </summary>
    public Func<CancellationToken, Task> WorkItem { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="QueuedTask"/> struct.
    /// </summary>
    /// <param name="sequenceNumber">The unique sequence number of the work item.</param>
    /// <param name="workItem">The delegate representing the work to perform.</param>
    public QueuedTask(long sequenceNumber, Func<CancellationToken, Task> workItem) =>
        (SequenceNumber, WorkItem) = (sequenceNumber, workItem);
}