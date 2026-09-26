namespace CQRSharp.Core.Outbox;

/// <summary>
///     Wakes the outbox processor of this process. The framework signals it after it stores outbox messages, so a
///     message stored here is picked up right away instead of after the polling interval; a custom store or producer
///     may signal it too.
/// </summary>
public interface IOutboxSignal
{
    /// <summary>Asks the processor to poll the store as soon as it can. Cheap and safe to call from anywhere.</summary>
    void Signal();
}

/// <summary>
///     The signal itself: a level-triggered flag the processor waits on alongside its polling delay. A signal that
///     arrives while a poll is running is not lost — the next wait returns at once — and many signals coalesce.
/// </summary>
internal sealed class OutboxSignal : IOutboxSignal
{
    private volatile TaskCompletionSource<bool> _pending = NewSource();

    public void Signal() => _pending.TrySetResult(true);

    /// <summary>
    ///     Waits until the signal is raised or <paramref name="delay" /> elapses on <paramref name="timeProvider" />'s clock.
    /// </summary>
    public async Task WaitAsync(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var current = _pending;
        if (!current.Task.IsCompleted)
        {
            using var timeout = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var timer = Task.Delay(delay, timeProvider, linked.Token);
            var completed = await Task.WhenAny(current.Task, timer).ConfigureAwait(false);
            if (completed == timer)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            // Cancel the pending delay's timer so it does not linger until the interval elapses.
            timeout.Cancel();
        }

        // Consumed: the next wait needs a fresh flag. A Signal() racing this swap lands on the new source and is kept.
        Interlocked.CompareExchange(ref _pending, NewSource(), current);
    }

    private static TaskCompletionSource<bool> NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
