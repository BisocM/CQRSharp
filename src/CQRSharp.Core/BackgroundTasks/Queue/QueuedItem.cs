namespace CQRSharp.Core.BackgroundTasks;

/// <summary>
///     One work item the queue accepted: the work, its caller's completion, and whether it has started. Exactly one of
///     four things ends its wait: the consumer starts it, its caller gives up, shutdown cancels it, or the queue evicts
///     it. Whichever comes first decides the caller's outcome; the others find the item already settled.
/// </summary>
internal abstract class QueuedItem
{
    private const int Waiting = 0;
    private const int Started = 1;
    private const int Settled = 2;

    private CancellationTokenRegistration _callerGaveUp;
    private int _state;

    /// <summary>Creates a waiting item.</summary>
    /// <param name="enqueuedAt">The <see cref="TimeProvider" /> timestamp at which the queue accepted the item.</param>
    protected QueuedItem(long enqueuedAt) => EnqueuedAt = enqueuedAt;

    /// <summary>The <see cref="TimeProvider" /> timestamp at which the queue accepted the item.</summary>
    public long EnqueuedAt { get; }

    /// <summary>Whether the item is still waiting: not started, and not cancelled or rejected.</summary>
    public bool IsWaiting => Volatile.Read(ref _state) == Waiting;

    /// <summary>
    ///     Withdraws the item when <paramref name="cancellationToken" /> fires while it is still waiting, so a caller that
    ///     gives up stops waiting at once instead of when the item reaches the head of the queue. Called once, before
    ///     the item is written, so the registration is in place before the consumer can see the item; a token that has
    ///     already fired settles the item on the spot.
    /// </summary>
    public void WithdrawOn(CancellationToken cancellationToken)
    {
        if (cancellationToken.CanBeCanceled)
            _callerGaveUp = cancellationToken.UnsafeRegister(
                static (item, token) => ((QueuedItem)item!).TryCancel(token), this);
    }

    /// <summary>
    ///     Claims the item for execution. <see langword="false" /> when it was settled while it waited; its caller has
    ///     already been answered and nothing may run.
    /// </summary>
    public bool TryStart()
    {
        if (Interlocked.CompareExchange(ref _state, Started, Waiting) != Waiting)
            return false;

        // A started item belongs to its work: the caller's token reaches it through the work's own token, not here.
        _callerGaveUp.Unregister();
        return true;
    }

    /// <summary>Settles a waiting item as cancelled. <see langword="false" /> when it had already started or settled.</summary>
    public bool TryCancel(CancellationToken cancellationToken)
    {
        if (!TrySettle()) return false;
        SetCanceled(cancellationToken);
        return true;
    }

    /// <summary>Settles a waiting item as failed. <see langword="false" /> when it had already started or settled.</summary>
    public bool TryReject(Exception exception)
    {
        if (!TrySettle()) return false;
        SetException(exception);
        return true;
    }

    /// <summary>
    ///     Runs the work and completes the caller's task with its outcome. Never throws: a failing or cancelled work
    ///     item faults or cancels the caller's task instead. Only for an item <see cref="TryStart" /> claimed.
    /// </summary>
    public abstract Task RunAsync(CancellationToken cancellationToken);

    /// <summary>Completes the caller's task as cancelled by <paramref name="cancellationToken" />.</summary>
    protected abstract void SetCanceled(CancellationToken cancellationToken);

    /// <summary>Completes the caller's task as faulted with <paramref name="exception" />.</summary>
    protected abstract void SetException(Exception exception);

    private bool TrySettle()
    {
        if (Interlocked.CompareExchange(ref _state, Settled, Waiting) != Waiting)
            return false;

        // Unregistering from inside the registration's own callback is a no-op, not a wait, so the caller's token can
        // settle the item through this path too.
        _callerGaveUp.Unregister();
        return true;
    }
}
