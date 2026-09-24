namespace CQRSharp.Core.BackgroundTasks;

/// <summary>A queued work item with a result (<see cref="IBackgroundTaskManager.EnqueueAsync{TResult}" />, and every <see cref="RunMode.Queued" /> dispatch).</summary>
internal sealed class QueuedWork<TResult>(Func<CancellationToken, Task<TResult>> work, long enqueuedAt) : QueuedItem(enqueuedAt)
{
    private readonly TaskCompletionSource<TResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The caller's task.</summary>
    public Task<TResult> Completion => _completion.Task;

    /// <inheritdoc />
    public override async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            _completion.TrySetResult(await work(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException ex)
        {
            _completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            _completion.TrySetException(ex);
        }
    }

    /// <inheritdoc />
    protected override void SetCanceled(CancellationToken cancellationToken) => _completion.TrySetCanceled(cancellationToken);

    /// <inheritdoc />
    protected override void SetException(Exception exception) => _completion.TrySetException(exception);
}
