using CQRSharp.Shared.Data.Interfaces.Notifications;

namespace CQRSharp.Core.Notifications.Types;

/// <summary>
///     Notification published when a background work item is successfully queued.
/// </summary>
public sealed class TaskEnqueuedNotification : INotification
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="TaskEnqueuedNotification" /> class.
    /// </summary>
    /// <param name="sequenceNumber">The sequence number of the enqueued task.</param>
    /// <param name="workItem">The delegate representing the task work item.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="workItem" /> is null.</exception>
    public TaskEnqueuedNotification(long sequenceNumber, Func<CancellationToken, Task> workItem)
    {
        SequenceNumber = sequenceNumber;
        WorkItem = workItem ?? throw new ArgumentNullException(nameof(workItem));
    }

    /// <summary>
    ///     Gets the sequence number assigned to the enqueued task.
    /// </summary>
    public long SequenceNumber { get; }

    /// <summary>
    ///     Gets the work item delegate representing the task to be executed.
    /// </summary>
    public Func<CancellationToken, Task> WorkItem { get; }
}