namespace CQRSharp.Core.BackgroundTasks.Types;

/// <summary>
///     Result of an <c>Enqueue/Write</c> attempt.
/// </summary>
public readonly struct QueueWriteResult(QueueWriteResultCode result, long sequenceNumber)
{
    /// <summary>
    ///     Gets the result code of the enqueue attempt.
    /// </summary>
    public QueueWriteResultCode Result { get; } = result;

    /// <summary>
    ///     Gets the sequence number assigned to the enqueued work item.
    /// </summary>
    public long SequenceNumber { get; } = sequenceNumber;
}