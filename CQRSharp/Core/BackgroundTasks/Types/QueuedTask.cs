namespace CQRSharp.Core.BackgroundTasks.Types;

/// <summary>
///     Wraps a background work‑item delegate together with a sequence
///     number and the identifier of the queue that produced it.
/// </summary>
public readonly struct QueuedTask
{
    /// <summary> Identifier of the originating queue.  Used to avoid cross‑talk when multiple queues live in the same process.</summary>
    internal Guid QueueId { get; }

    /// <summary> The time at which the task was enqueued. </summary>
    public DateTime EnqueueTime { get; }

    /// <summary>Monotonically increasing sequence number (unique within one queue).</summary>
    public long SequenceNumber { get; }

    /// <summary>The actual work to perform.</summary>
    public Func<CancellationToken, Task> WorkItem { get; }

    /// <summary>Creates a new task wrapper.</summary>
    /// <param name="queueId">Unique identifier of the queue.</param>
    /// <param name="sequenceNumber">Sequence number assigned by that queue.</param>
    /// <param name="workItem">Delegate encapsulating the work.</param>
    /// <param name="enqueueTime">The time at which the task was enqueued. Equal to <see cref="DateTime.UtcNow" /> by default.</param>
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