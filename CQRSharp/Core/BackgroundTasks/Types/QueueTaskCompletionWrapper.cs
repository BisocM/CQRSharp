namespace CQRSharp.Core.BackgroundTasks.Types;

/// <summary>
/// Wraps a background task's completion so it can be signaled if the task is dropped
/// or faults before execution.
/// </summary>
internal class QueueTaskCompletionWrapper(Action cancelAction, Action<Exception> exceptionAction)
{
    /// <summary>Action to invoke to cancel the awaiting TaskCompletionSource.</summary>
    public Action CancelAction { get; } = cancelAction;

    /// <summary>Action to invoke to fault the awaiting TaskCompletionSource with an exception.</summary>
    public Action<Exception> ExceptionAction { get; } = exceptionAction;
}