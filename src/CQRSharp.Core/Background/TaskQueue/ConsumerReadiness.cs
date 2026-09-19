namespace CQRSharp.Core.Background.TaskQueue;

/// <summary>
///     A one-shot readiness signal the background queue consumer completes when its loop starts. A
///     <see cref="CQRSharp.Core.Options.Enums.RunMode.Queued" /> dispatch waits on it (bounded by
///     <see cref="CQRSharp.Core.Options.BackgroundTaskQueueOptions.ConsumerStartTimeout" />) before handing work over,
///     so it fails loudly when the host never starts the consumer instead of hanging forever on a task nothing runs.
/// </summary>
internal sealed class ConsumerReadiness
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the background queue consumer's processing loop has started.</summary>
    public Task Started => _started.Task;

    /// <summary>Signals that the consumer loop has started. Idempotent.</summary>
    public void MarkStarted() => _started.TrySetResult();
}
