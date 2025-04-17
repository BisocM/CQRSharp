namespace CQRSharp.Core.BackgroundTasks.Types;

/// <summary>
/// Describes the outcome of a background work item enqueue attempt, including a sequence number.
/// </summary>
public readonly struct QueueWriteResult
{
    /// <summary>
    /// Gets the result code of the enqueue attempt.
    /// </summary>
    public QueueWriteResultCode Result { get; }

    /// <summary>
    /// Gets the sequence number assigned to the enqueued work item.
    /// </summary>
    public long SequenceNumber { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueWriteResult"/> struct.
    /// </summary>
    /// <param name="result">The result code of the enqueue attempt.</param>
    /// <param name="sequenceNumber">The unique sequence number of the work item.</param>
    public QueueWriteResult(QueueWriteResultCode result, long sequenceNumber)
    {
        Result = result;
        SequenceNumber = sequenceNumber;
    }
}