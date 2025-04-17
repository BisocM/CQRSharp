using System.Threading.Channels;

namespace CQRSharp.Core.BackgroundTasks.Types;

/// <summary>
/// Provides detailed information when a work item is rejected due to queue overflow.
/// </summary>
public readonly struct TaskRejectedEventArgs
{
    /// <summary>
    /// Gets the sequence number of the work item that was rejected.
    /// </summary>
    public long SequenceNumber { get; }

    /// <summary>
    /// Gets the configured policy that triggered the rejection.
    /// </summary>
    public BoundedChannelFullMode Policy { get; }

    /// <summary>
    /// Gets the sequence number of the item dropped from the queue, if any.
    /// </summary>
    public long? DroppedSequenceNumber { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskRejectedEventArgs"/> struct.
    /// </summary>
    /// <param name="sequenceNumber">The sequence number of the rejected work item.</param>
    /// <param name="policy">The backpressure policy causing the rejection.</param>
    /// <param name="droppedSequenceNumber">The sequence number of the dropped item, if one was removed.</param>
    public TaskRejectedEventArgs(long sequenceNumber, BoundedChannelFullMode policy, long? droppedSequenceNumber)
    {
        SequenceNumber = sequenceNumber;
        Policy = policy;
        DroppedSequenceNumber = droppedSequenceNumber;
    }
}