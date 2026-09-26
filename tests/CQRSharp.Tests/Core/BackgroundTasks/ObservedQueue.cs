using CQRSharp.Core.BackgroundTasks;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Hands the consumer the real queue while signalling the moment the consumer closes it, so a shutdown test can act
///     inside the drain window deterministically instead of polling for it.
/// </summary>
internal sealed class ObservedQueue(IBackgroundTaskQueue inner) : IBackgroundTaskQueue
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes the first time the consumer calls <see cref="CompleteAdding" />.</summary>
    public Task Closed => _closed.Task;

    public ValueTask<QueuedItem?> DequeueAsync(CancellationToken cancellationToken) => inner.DequeueAsync(cancellationToken);

    public void CompleteAdding()
    {
        inner.CompleteAdding();
        _closed.TrySetResult();
    }

    public int CancelPending() => inner.CancelPending();
}
