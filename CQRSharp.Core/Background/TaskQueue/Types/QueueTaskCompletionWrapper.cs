namespace CQRSharp.Core.Background.TaskQueue.Types;

/// <summary>
///     Holds the two callbacks required to
///     • cancel a <see cref="TaskCompletionSource{TResult}" /> and
///     • fault it with an <see cref="Exception" /> –
///     for a task that was <em>scheduled</em> but never <em>executed</em>.
/// </summary>
internal sealed class QueueTaskCompletionWrapper(
    Action cancelAction,
    Action<Exception> exceptionAction)
{
    public Action CancelAction { get; } = cancelAction ?? throw new ArgumentNullException(nameof(cancelAction));

    public Action<Exception> ExceptionAction { get; } =
        exceptionAction ?? throw new ArgumentNullException(nameof(exceptionAction));
}