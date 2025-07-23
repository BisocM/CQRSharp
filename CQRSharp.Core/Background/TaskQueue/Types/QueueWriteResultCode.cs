namespace CQRSharp.Core.Background.TaskQueue.Types;

/// <summary>
///     Indicates the result of attempting to write a task into the background queue.
/// </summary>
public enum QueueWriteResultCode
{
    /// <summary>
    ///     The work item was successfully enqueued.
    /// </summary>
    Enqueued,

    /// <summary>
    ///     The newest work item was dropped due to a full queue policy.
    /// </summary>
    DroppedNewest,

    /// <summary>
    ///     The oldest work item was dropped to make room for a new one.
    /// </summary>
    DroppedOldest,

    /// <summary>
    ///     The write operation waited until space became available.
    /// </summary>
    Waited
}