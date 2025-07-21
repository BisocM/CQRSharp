namespace CQRSharp.Core.BackgroundTasks.Types;

/// <summary>
///     Represents a work item enqueued for background execution.
/// </summary>
public readonly struct QueuedTask
{
    /// <summary>
    ///     Gets the identifier of the originating queue.
    /// </summary>
    public Guid QueueId { get; }

    /// <summary>
    ///     Gets the time at which the task was enqueued.
    /// </summary>
    public DateTime EnqueueTime { get; }

    /// <summary>
    ///     Gets the sequence number assigned to this task.
    /// </summary>
    public long SequenceNumber { get; }

    /// <summary>
    ///     Gets the delegate encapsulating the work to perform.
    /// </summary>
    public Func<CancellationToken, Task> WorkItem { get; }

    /// <summary>
    ///     Initializes a new instance of <see cref="QueuedTask" />.
    /// </summary>
    /// <param name="queueId">The unique identifier of the queue.</param>
    /// <param name="sequenceNumber">The sequence number for this task.</param>
    /// <param name="workItem">The work delegate.</param>
    /// <param name="enqueueTime">The timestamp when the task was enqueued.</param>
    public QueuedTask(
        Guid queueId,
        long sequenceNumber,
        Func<CancellationToken, Task> workItem,
        DateTime enqueueTime)
    {
        QueueId = queueId;
        SequenceNumber = sequenceNumber;
        WorkItem = workItem ?? throw new ArgumentNullException(nameof(workItem));
        EnqueueTime = enqueueTime;
    }
}