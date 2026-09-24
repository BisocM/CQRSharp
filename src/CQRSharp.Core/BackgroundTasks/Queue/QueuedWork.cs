namespace CQRSharp.Core.BackgroundTasks;

/// <summary>A queued work item without a result (<see cref="IBackgroundTaskManager.EnqueueAsync(Func{CancellationToken, Task}, CancellationToken)" />).</summary>
internal sealed class QueuedWork(Func<CancellationToken, Task> work, long enqueuedAt) : QueuedItem(enqueuedAt)
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The caller's task.</summary>
    public Task Completion => _completion.Task;

    /// <inheritdoc />
    public override async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await work(cancellationToken).ConfigureAwait(false);
            _completion.TrySetResult();
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
