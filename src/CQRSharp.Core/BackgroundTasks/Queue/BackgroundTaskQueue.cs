using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.BackgroundTasks;

/// <summary>
///     The bounded queue behind <see cref="IBackgroundTaskManager" /> and <see cref="RunMode.Queued" /> dispatch: a
///     channel of work items that <see cref="BackgroundTaskQueueConsumer" /> runs. Every caller's task completes: with the
///     work's own outcome once it ran; cancelled when the caller gave up, or shutdown cancelled it, before it started; or
///     faulted with <see cref="BackgroundTaskRejectedException" /> when the queue refused or evicted it.
/// </summary>
internal sealed class BackgroundTaskQueue : IBackgroundTaskQueue, IBackgroundTaskManager, IDisposable
{
    // What queued work cancelled by shutdown observes: a token that really is cancelled, owned by nobody.
    private static readonly CancellationToken ShutdownCancellation = new(canceled: true);

    private readonly int _capacity;
    private readonly Channel<QueuedItem> _channel;
    private readonly BoundedChannelFullMode _fullMode;
    private readonly BackgroundTaskQueueMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private volatile bool _closed;

    /// <summary>Creates the queue from its options.</summary>
    /// <param name="options">The queue's capacity and full mode.</param>
    /// <param name="timeProvider">The clock the wait-time histogram reads; defaults to <see cref="TimeProvider.System" />.</param>
    /// <param name="meterFactory">The provider's meter factory, which creates and owns the queue's meter when registered.</param>
    public BackgroundTaskQueue(IOptions<BackgroundTaskQueueOptions> options, TimeProvider? timeProvider = null, IMeterFactory? meterFactory = null)
    {
        // Capacity is validated with the options (BackgroundTaskQueueOptionsValidator), and the channel below rejects a
        // capacity below one on its own.
        var settings = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _capacity = settings.Capacity;
        _fullMode = settings.FullMode;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // DropWrite is enforced here, not by the channel: the channel's own DropWrite reports a dropped write as
        // accepted, and a refused caller must be told so. The evicting modes stay with the channel, which hands each
        // evicted item to Evict outside its lock.
        _channel = Channel.CreateBounded<QueuedItem>(
            new BoundedChannelOptions(_capacity)
            {
                FullMode = _fullMode == BoundedChannelFullMode.DropWrite ? BoundedChannelFullMode.Wait : _fullMode,
                // The shutdown's CancelPending can read alongside a consumer that is still draining.
                SingleReader = false,
                SingleWriter = false,
                // A producer's write must not run the consumer loop's continuation, nor a dequeue a waiting producer's.
                AllowSynchronousContinuations = false
            },
            Evict);

        _metrics = new BackgroundTaskQueueMetrics(() => _channel.Reader.Count, meterFactory);
    }

    /// <summary>The queue's meter, so a test can listen to this queue's instruments alone.</summary>
    internal Meter Meter => _metrics.Meter;

    /// <inheritdoc />
    public Task EnqueueAsync(Func<CancellationToken, Task> workItem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        var item = new QueuedWork(workItem, _timeProvider.GetTimestamp());
        Accept(item, cancellationToken);
        return item.Completion;
    }

    /// <inheritdoc />
    public Task<TResult> EnqueueAsync<TResult>(Func<CancellationToken, Task<TResult>> workItem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        var item = new QueuedWork<TResult>(workItem, _timeProvider.GetTimestamp());
        Accept(item, cancellationToken);
        return item.Completion;
    }

    /// <inheritdoc />
    public async ValueTask<QueuedItem?> DequeueAsync(CancellationToken cancellationToken)
    {
        var reader = _channel.Reader;
        while (true)
        {
            // A dequeue whose token has fired takes nothing, even when an item is ready: the consumer can win a slot
            // after shutdown began (a semaphore may grant a wait its token has just cancelled), and whatever is still
            // queued then belongs to the shutdown's drain or cancellation.
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.TryRead(out var item))
            {
                // An item its caller withdrew keeps its place until it is reached; here it leaves without running.
                if (!item.TryStart())
                    continue;

                if (_metrics.WaitDurationEnabled)
                    _metrics.RecordWait(_timeProvider.GetElapsedTime(item.EnqueuedAt));

                return item;
            }

            if (!await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                return null;
        }
    }

    /// <inheritdoc />
    public void CompleteAdding()
    {
        // Set before completing, so a DropWrite refusal that races the completion is reported as closed.
        _closed = true;
        _channel.Writer.TryComplete();
    }

    /// <inheritdoc />
    public int CancelPending()
    {
        var cancelled = 0;
        while (_channel.Reader.TryRead(out var item))
            if (item.TryCancel(ShutdownCancellation))
                cancelled++;

        return cancelled;
    }

    /// <summary>Stops accepting work, cancels what is still queued, and disposes the queue's meter.</summary>
    public void Dispose()
    {
        CompleteAdding();
        CancelPending();
        _metrics.Dispose();
    }

    private void Accept(QueuedItem item, CancellationToken cancellationToken)
    {
        item.WithdrawOn(cancellationToken);
        if (!item.IsWaiting)
            return; // The caller had given up already.

        if (_channel.Writer.TryWrite(item))
        {
            _metrics.Enqueued();
            return;
        }

        // The channel refuses a write only when it is full in Wait mode (which DropWrite runs as) or completed.
        if (_fullMode == BoundedChannelFullMode.Wait)
            _ = WaitForRoomAsync(item, cancellationToken);
        else if (_fullMode == BoundedChannelFullMode.DropWrite && !_closed)
            Reject(item, BackgroundTaskRejectionReason.QueueFull);
        else
            Reject(item, BackgroundTaskRejectionReason.QueueClosed);
    }

    // Settles the item on every path, so nothing the caller awaits is left pending; the returned task never faults.
    private async Task WaitForRoomAsync(QueuedItem item, CancellationToken cancellationToken)
    {
        try
        {
            await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            _metrics.Enqueued();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller's registration usually got here first, but the write's own cancellation can complete sooner.
            item.TryCancel(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            Reject(item, BackgroundTaskRejectionReason.QueueClosed);
        }
        catch (Exception ex)
        {
            item.TryReject(ex);
        }
    }

    private void Reject(QueuedItem item, BackgroundTaskRejectionReason reason)
    {
        var message = reason == BackgroundTaskRejectionReason.QueueFull
            ? $"The background task queue is full ({_capacity} work items) and its FullMode is DropWrite, so it refused the work item."
            : "The background task queue no longer accepts work: the host is shutting down, or the queue was disposed.";

        if (item.TryReject(new BackgroundTaskRejectedException(reason, message)))
            _metrics.Rejected(reason);
    }

    private void Evict(QueuedItem item)
    {
        var rejection = new BackgroundTaskRejectedException(
            BackgroundTaskRejectionReason.Evicted,
            $"The background task queue was full and its FullMode is {_fullMode}, so it evicted this queued work item to make room for newer work.");

        // An item its caller had already withdrawn was no longer work anyone waits for, so it is not counted as lost.
        if (item.TryReject(rejection))
            _metrics.Evicted(_fullMode);
    }
}
