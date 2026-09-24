using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.BackgroundTasks;

/// <summary>
///     The hosted service that runs the background task queue's work items, at most
///     <see cref="BackgroundTaskQueueOptions.ConsumerCount" /> at a time, and drains the queue when the host stops.
/// </summary>
/// <remarks>
///     One dispatch loop takes a slot, dequeues and hands the item off to the thread pool; the item gives its slot back
///     when it finishes. So the number of slots in use is the number of items running, which is also what shutdown
///     waits for.
/// </remarks>
internal sealed partial class BackgroundTaskQueueConsumer : BackgroundService
{
    // The queue whose work item this async flow is running, if any. Set inside each item's own flow, so it never leaks
    // back into the dispatch loop. The container's one BackgroundTaskQueue is both the consumer's IBackgroundTaskQueue
    // and the executor's IBackgroundTaskManager, so a reference comparison identifies it from either side.
    private static readonly AsyncLocal<IBackgroundTaskQueue?> RunningItemOf = new();

    private readonly bool _drainOnShutdown;
    private readonly ILogger<BackgroundTaskQueueConsumer> _logger;
    private readonly int _maxConcurrency;
    private readonly IBackgroundTaskQueue _queue;
    private readonly ConsumerReadiness _readiness;
    private readonly TimeSpan _shutdownTimeout;

    // Never disposed: work still running after a timed-out shutdown gives its slot back into it later, and without a
    // wait handle it holds nothing to release.
    private readonly SemaphoreSlim _slots;
    private readonly TimeProvider _timeProvider;

    // The token handed to work items. Deliberately NOT the host's stoppingToken: that fires the instant shutdown
    // begins, which would abort every in-flight handler immediately. In-flight work keeps running through the
    // ShutdownTimeout grace period and is cancelled only once that period is exhausted.
    private readonly CancellationTokenSource _workCancellation = new();

    /// <summary>Creates the consumer.</summary>
    /// <param name="queue">The queue to run work items from.</param>
    /// <param name="logger">The consumer's logger.</param>
    /// <param name="options">The concurrency limit and shutdown settings.</param>
    /// <param name="readiness">The readiness signal completed when the dispatch loop starts.</param>
    /// <param name="timeProvider">The clock the shutdown budget runs on; defaults to <see cref="TimeProvider.System" />.</param>
    public BackgroundTaskQueueConsumer(
        IBackgroundTaskQueue queue,
        ILogger<BackgroundTaskQueueConsumer> logger,
        IOptions<BackgroundTaskQueueOptions> options,
        ConsumerReadiness readiness,
        TimeProvider? timeProvider = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        _timeProvider = timeProvider ?? TimeProvider.System;

        var settings = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _maxConcurrency = settings.ConsumerCount > 0 ? settings.ConsumerCount : Environment.ProcessorCount;
        _slots = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
        _shutdownTimeout = settings.ShutdownTimeout;
        _drainOnShutdown = settings.DrainOnShutdown;
    }

    /// <summary>
    ///     Whether the current async flow is running a work item of <paramref name="queue" />. A
    ///     <see cref="RunMode.Queued" /> dispatch made from such a flow must run at once: queued behind its caller, it
    ///     would hold the caller's slot while it waits for one of its own. Keyed by queue, so a request sent into another
    ///     host's dispatcher is that host's to queue.
    /// </summary>
    internal static bool IsRunningWorkItemOf(IBackgroundTaskManager queue) => ReferenceEquals(RunningItemOf.Value, queue);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(_logger, _maxConcurrency);

        // Signal readiness as soon as the loop starts so a RunMode.Queued dispatch can tell the consumer is alive
        // (and won't hang waiting for work that nothing would drain).
        _readiness.MarkStarted();

        try
        {
            await ConsumeAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping; the shutdown below drains what is left.
        }
        catch (Exception ex)
        {
            LogLoopFailed(_logger, ex);
        }
        finally
        {
            await ShutDownAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        LogStopping(_logger);

        // Cancels the stopping token; ExecuteAsync's shutdown then drains within ShutdownTimeout. This returns once that
        // is done, or once the host's own token fires first.
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // The host has stopped being patient: do not leave in-flight work running past its forced shutdown.
        if (cancellationToken.IsCancellationRequested)
            _workCancellation.Cancel();

        // ExecuteAsync's own shutdown normally did both of these already (they are idempotent). But it is not
        // guaranteed to have run at all: since .NET 10 a BackgroundService schedules ExecuteAsync on the thread pool,
        // so a host that stops during startup cancels it before its first line. Without this the queue would keep
        // accepting work that nothing will ever execute, and every caller awaiting it would hang.
        _queue.CompleteAdding();
        CancelQueuedWork();
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        base.Dispose();

        // The work token source is disposed only once nothing can touch it any more. A host that disposes a running
        // consumer (no StopAsync, or a forced stop that returned while the drain was still in flight) keeps it alive:
        // ConsumeAsync reads its token per item and the drain may still cancel it, and both throw on a disposed source.
        if (ExecuteTask is null || ExecuteTask.IsCompleted)
            _workCancellation.Dispose();
    }

    // Runs until the token fires (throws) or the queue is completed and empty (returns).
    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            // The slot comes BEFORE the dequeue: an item dequeued first and then stranded by a cancelled slot wait would
            // never run, and its caller would never hear back.
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);

            QueuedItem? item;
            try
            {
                item = await _queue.DequeueAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _slots.Release();
                throw;
            }

            if (item is null)
            {
                _slots.Release();
                return;
            }

            _ = RunAsync(item, _workCancellation.Token);
        }
    }

    // Never throws; gives the item's slot back when it finishes.
    private async Task RunAsync(QueuedItem item, CancellationToken cancellationToken)
    {
        // Leave the dispatch loop before running any of the item: an item that blocks or computes before its first real
        // await would otherwise hold the loop, so nothing else could start (ConsumerCount would not be honoured) and the
        // shutdown deadline could not interrupt the drain. ForceYielding, unlike Task.Yield, always continues on the
        // thread pool, even when the loop's first iteration runs on a host-starting thread with a SynchronizationContext.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        RunningItemOf.Value = _queue;
        try
        {
            await item.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The item hands every outcome of its work to its caller; only a defect in that hand-off lands here.
            LogRunFailed(_logger, ex);
        }
        finally
        {
            _slots.Release();
        }
    }

    // One ShutdownTimeout budget covers both the queued backlog and the in-flight work. New work is refused from the
    // first moment; whatever the budget does not cover is cancelled, so no caller awaiting a queued item is left hanging.
    private async Task ShutDownAsync()
    {
        // Created before the queue closes, so the budget is already running when anything can observe the shutdown.
        using var deadline = new CancellationTokenSource(_shutdownTimeout, _timeProvider);
        try
        {
            _queue.CompleteAdding();

            // Without the drain nothing will run the backlog, so its callers are released now rather than after the
            // running work, or the whole budget; the final CancelQueuedWork below catches what a timed-out drain leaves.
            if (_drainOnShutdown)
                await ConsumeAsync(deadline.Token).ConfigureAwait(false);
            else
                CancelQueuedWork();

            var running = _maxConcurrency - _slots.CurrentCount;
            if (running > 0)
                LogWaitingForRunningWork(_logger, running);

            // Every slot back means nothing is running any more.
            for (var i = 0; i < _maxConcurrency; i++)
                await _slots.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LogShutdownTimedOut(_logger, _shutdownTimeout);
            _workCancellation.Cancel();
        }
        catch (Exception ex)
        {
            LogShutdownFailed(_logger, ex);
        }

        CancelQueuedWork();
        LogStopped(_logger);
    }

    // Fails every queued item that will not get to run, so no caller awaiting one is left hanging. Idempotent.
    private void CancelQueuedWork()
    {
        var cancelled = _queue.CancelPending();
        if (cancelled > 0)
            LogQueuedWorkCancelled(_logger, cancelled);
    }

    // Start and stop are Debug: the consumer runs in every host, most of which never queue anything.
    [LoggerMessage(1100, LogLevel.Debug, "Background task queue consumer started; up to {MaxConcurrency} work items run at once.")]
    private static partial void LogStarted(ILogger logger, int maxConcurrency);

    [LoggerMessage(1101, LogLevel.Debug, "Background task queue consumer stopping.")]
    private static partial void LogStopping(ILogger logger);

    [LoggerMessage(1102, LogLevel.Critical, "The background task queue consumer loop failed; the queue no longer accepts work.")]
    private static partial void LogLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(1103, LogLevel.Information, "Waiting for {Count} running background work item(s) to finish.")]
    private static partial void LogWaitingForRunningWork(ILogger logger, int count);

    [LoggerMessage(1104, LogLevel.Warning, "The background task queue's shutdown budget of {ShutdownTimeout} ran out; canceling the work still running.")]
    private static partial void LogShutdownTimedOut(ILogger logger, TimeSpan shutdownTimeout);

    [LoggerMessage(1105, LogLevel.Error, "Shutting down the background task queue consumer failed.")]
    private static partial void LogShutdownFailed(ILogger logger, Exception exception);

    [LoggerMessage(1106, LogLevel.Warning, "Canceled {Count} queued background work item(s) that did not get to run before shutdown.")]
    private static partial void LogQueuedWorkCancelled(ILogger logger, int count);

    [LoggerMessage(1107, LogLevel.Error, "A background work item could not hand its outcome to its caller.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);

    [LoggerMessage(1108, LogLevel.Debug, "Background task queue consumer stopped.")]
    private static partial void LogStopped(ILogger logger);
}
