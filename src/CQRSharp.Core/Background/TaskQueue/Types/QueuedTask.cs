namespace CQRSharp.Core.Background.TaskQueue.Types;

/// <summary>
///     Represents a work item that has been enqueued for background processing.
/// </summary>
public readonly struct QueuedTask
{
    /// <summary>
    ///     The ID of the queue instance that this task belongs to.
    /// </summary>
    public Guid QueueId { get; }

    /// <summary>
    ///     The sequential number of this task, unique within its queue instance.
    /// </summary>
    public long SequenceNumber { get; }

    /// <summary>
    ///     The delegate representing the asynchronous work to be performed.
    /// </summary>
    public Func<CancellationToken, Task> WorkItem { get; }

    /// <summary>
    ///     The UTC timestamp when the task was enqueued.
    /// </summary>
    public DateTime EnqueueTime { get; }

    /// <summary>
    ///     Initializes a new instance of the <see cref="QueuedTask" /> struct.
    /// </summary>
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