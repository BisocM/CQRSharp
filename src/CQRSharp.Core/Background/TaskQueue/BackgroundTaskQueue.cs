using System.Threading.Channels;
using CQRSharp.Pipelines;
using CQRSharp.Core.Background.TaskQueue.Telemetry;
using CQRSharp.Core.Background.TaskQueue.Types;
using CQRSharp.Core.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Background.TaskQueue;

/// <summary>
///     Provides a thread-safe queue for background work items, supporting enqueueing and a consumption mechanism
///     for a background service. It also handles internal notifications about queue state changes.
/// </summary>
internal sealed partial class BackgroundTaskQueue : IBackgroundTaskQueue, IBackgroundTaskManager, IDisposable
{
    private readonly QueueEntry[] _buffer;
    private readonly CancellationTokenSource _completion = new();
    private readonly SemaphoreSlim? _freeSlots;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _itemsAvailable;
    private readonly ILogger<BackgroundTaskQueue> _logger;
    private readonly IQueueMetricsReporter _metrics;
    private readonly bool _metricsEnabled;
    private readonly Channel<INotification> _notificationChannel;
    private readonly SemaphoreSlim _notifSem;
    private readonly BackgroundTaskQueueOptions _options;
    private readonly Task _pumpTask;
    private readonly Guid _queueId = Guid.NewGuid();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CancellationToken _shutdownToken;
    private readonly TimeProvider _timeProvider;
    private int _count;
    private int _disposed;
    private int _head;
    private bool _isCompleted;

    private long _sequenceCounter;
    private int _tail;

    /// <summary>
    ///     Initializes a new instance of the <see cref="BackgroundTaskQueue" /> class.
    /// </summary>
    public BackgroundTaskQueue(
        IOptions<BackgroundTaskQueueOptions> options,
        IServiceScopeFactory scopeFactory,
        IQueueMetricsReporter metrics,
        ILogger<BackgroundTaskQueue> logger,
        IHostApplicationLifetime? lifetime = null,
        TimeProvider? timeProvider = null)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _shutdownToken = lifetime?.ApplicationStopping ?? _completion.Token;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _metricsEnabled = _options.EnableMetrics;

        if (_options.Capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(_options.Capacity), "Capacity must be greater than zero.");

        _buffer = new QueueEntry[_options.Capacity];
        _itemsAvailable = new SemaphoreSlim(0, _options.Capacity);

        _freeSlots = _options.FullMode == BoundedChannelFullMode.Wait
            ? new SemaphoreSlim(_options.Capacity, _options.Capacity)
            : null;

        _notificationChannel = Channel.CreateBounded<INotification>(
            new BoundedChannelOptions(_options.CallbackChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest, // Prevents slow notification handlers from blocking the queue
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

        _notifSem = new SemaphoreSlim(Environment.ProcessorCount, Environment.ProcessorCount);

        // Start the notification pump and keep a handle on it so Dispose can drain it and so a fault is observed
        // rather than swallowed by a fire-and-forget task.
        _pumpTask = ProcessNotificationsAsync(_shutdownToken);
        _ = _pumpTask.ContinueWith(
            t => _logger.LogCritical(t.Exception, "The notification pump terminated unexpectedly."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    /// <inheritdoc />
    public Task EnqueueAsync(Func<CancellationToken, Task> workItem, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = EnqueueInternalAsync(tcs, workItem, cancellationToken);
        return tcs.Task;
    }

    /// <inheritdoc />
    public Task<TResult> EnqueueAsync<TResult>(Func<CancellationToken, Task<TResult>> workItem, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = EnqueueInternalAsync(tcs, workItem, cancellationToken);
        return tcs.Task;
    }

    ValueTask<QueuedTask> IBackgroundTaskQueue.DequeueAsync(CancellationToken cancellationToken) =>
        DequeueAsync(cancellationToken);

    /// <summary>
    ///     Disposes resources used by the queue, such as completing the channels and disposing the semaphore.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        Complete();
        DrainAndCancelPending();

        _notificationChannel.Writer.TryComplete();

        // Best-effort: let the notification pump observe channel completion / cancellation and finish any in-flight
        // dispatches before we tear down the semaphore those dispatches release into. Delivery is best-effort during
        // shutdown; this bounds how long Dispose waits.
        try
        {
            _pumpTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException)
        {
            // The pump faulted or was cancelled during shutdown; its fault continuation already logged any error.
        }

        _notifSem.Dispose();
        _itemsAvailable.Dispose();
        _freeSlots?.Dispose();
        _completion.Dispose();
        _metrics.Dispose();
    }

    /// <summary>
    ///     Internal method to queue a work item. It handles different full-mode behaviors and reports metrics.
    /// </summary>
    internal Task<QueueWriteResult> QueueBackgroundWorkItemAsync(Func<CancellationToken, Task> workItem, CancellationToken cancellationToken) =>
        QueueBackgroundWorkItemAsync(workItem, cancellationToken, null, null);

    internal async Task<QueueWriteResult> QueueBackgroundWorkItemAsync(
        Func<CancellationToken, Task> workItem,
        CancellationToken cancellationToken,
        Action<Exception>? setException,
        Action<CancellationToken>? setCanceled)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        var sequence = Interlocked.Increment(ref _sequenceCounter);
        var queuedTask = new QueuedTask(_queueId, sequence, workItem, _timeProvider.GetUtcNow().UtcDateTime);
        var entry = new QueueEntry(queuedTask, setException, setCanceled);

        if (Volatile.Read(ref _disposed) != 0)
        {
            entry.Reject(new ObjectDisposedException(nameof(BackgroundTaskQueue)));
            return new QueueWriteResult(QueueWriteResultCode.DroppedNewest, sequence);
        }

        switch (_options.FullMode)
        {
            case BoundedChannelFullMode.DropWrite:
                return TryEnqueueDropWrite(entry);

            case BoundedChannelFullMode.Wait:
                return await EnqueueWaitAsync(entry, cancellationToken).ConfigureAwait(false);

            case BoundedChannelFullMode.DropOldest:
                return TryEnqueueDropOldest(entry);

            case BoundedChannelFullMode.DropNewest:
                return TryEnqueueDropNewest(entry);

            default:
                throw new NotSupportedException($"Unsupported BoundedChannelFullMode: {_options.FullMode}");
        }
    }

    private async Task EnqueueInternalAsync(
        TaskCompletionSource tcs,
        Func<CancellationToken, Task> workItem,
        CancellationToken cancellationToken)
    {
        async Task Wrapper(CancellationToken ct)
        {
            try
            {
                await workItem(ct).ConfigureAwait(false);
                tcs.TrySetResult();
            }
            catch (OperationCanceledException ex)
            {
                tcs.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        try
        {
            var result = await QueueBackgroundWorkItemAsync(
                Wrapper,
                cancellationToken,
                ex => tcs.TrySetException(ex),
                token => tcs.TrySetCanceled(token)).ConfigureAwait(false);

            if (result.Result is not (QueueWriteResultCode.Enqueued or QueueWriteResultCode.Waited))
                tcs.TrySetException(new ChannelClosedException());
        }
        catch (OperationCanceledException ex)
        {
            tcs.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
    }

    private async Task EnqueueInternalAsync<TResult>(
        TaskCompletionSource<TResult> tcs,
        Func<CancellationToken, Task<TResult>> workItem,
        CancellationToken cancellationToken)
    {
        async Task Wrapper(CancellationToken ct)
        {
            try
            {
                var result = await workItem(ct).ConfigureAwait(false);
                tcs.TrySetResult(result);
            }
            catch (OperationCanceledException ex)
            {
                tcs.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        try
        {
            var result = await QueueBackgroundWorkItemAsync(
                Wrapper,
                cancellationToken,
                ex => tcs.TrySetException(ex),
                token => tcs.TrySetCanceled(token)).ConfigureAwait(false);

            if (result.Result is not (QueueWriteResultCode.Enqueued or QueueWriteResultCode.Waited))
                tcs.TrySetException(new ChannelClosedException());
        }
        catch (OperationCanceledException ex)
        {
            tcs.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
    }
}
