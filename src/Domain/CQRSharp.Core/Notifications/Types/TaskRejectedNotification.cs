using System.Threading.Channels;
using CQRSharp.Abstractions.Data.Interfaces.Notifications;

namespace CQRSharp.Core.Notifications.Types;

/// <summary>
///     Notification published when a background work item is rejected due to back-pressure policy.
/// </summary>
public sealed class TaskRejectedNotification : INotification
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="TaskRejectedNotification" /> class.
    /// </summary>
    /// <param name="sequenceNumber">The sequence number of the rejected task.</param>
    /// <param name="fullMode">The channel full mode that triggered rejection.</param>
    public TaskRejectedNotification(long sequenceNumber, BoundedChannelFullMode fullMode)
    {
        SequenceNumber = sequenceNumber;
        FullMode = fullMode;
    }

    /// <summary>
    ///     Gets the sequence number of the task that was rejected.
    /// </summary>
    public long SequenceNumber { get; }

    /// <summary>
    ///     Gets the back-pressure policy that caused the task to be rejected.
    /// </summary>
    public BoundedChannelFullMode FullMode { get; }
}