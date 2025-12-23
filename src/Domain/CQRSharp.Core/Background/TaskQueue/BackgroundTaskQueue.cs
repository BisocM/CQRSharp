using System.Threading.Channels;
using CQRSharp.Abstractions.Data.Interfaces.Notifications;
using CQRSharp.Core.Background.TaskQueue.Telemetry;
using CQRSharp.Core.Background.TaskQueue.Types;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Notifications.Types;
using CQRSharp.Core.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Background.TaskQueue;

/// <summary>
///     Provides a thread-safe queue for background work items, supporting enqueueing and a consumption mechanism
///     for a background service. It also handles internal notifications about queue state changes.
/// </summary>
internal sealed class BackgroundTaskQueue : IBackgroundTaskQueue, IBackgroundTaskManager, IDisposable
{
    private readonly object _gate = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BackgroundTaskQueue> _logger;
    private readonly IQueueMetricsReporter _metrics;
    private readonly Channel<INotification> _notificationChannel;
    private readonly SemaphoreSlim _notifSem;
    private readonly BackgroundTaskQueueOptions _options;
    private readonly bool _metricsEnabled;
    private readonly Guid _queueId = Guid.NewGuid();
    private readonly SemaphoreSlim _itemsAvailable;
    private readonly SemaphoreSlim? _freeSlots;
    private readonly CancellationTokenSource _completion = new();
    private readonly CancellationToken _shutdownToken;

    private long _sequenceCounter;
    private int _disposed;
    private bool _isCompleted;

    private readonly QueueEntry[] _buffer;
    private int _head;
    private int _tail;
    private int _count;

    private readonly struct QueueEntry
    {
        public QueueEntry(
            QueuedTask task,
            Action<Exception>? setException,
            Action<CancellationToken>? setCanceled)
        {
            Task = task;
            SetException = setException;
            SetCanceled = setCanceled;
        }

        public QueuedTask Task { get; }
        public Action<Exception>? SetException { get; }
        public Action<CancellationToken>? SetCanceled { get; }

        public void Reject(Exception exception) => SetException?.Invoke(exception);
        public void Cancel(CancellationToken cancellationToken) => SetCanceled?.Invoke(cancellationToken);
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="BackgroundTaskQueue" /> class.
    /// </summary>
    public BackgroundTaskQueue(
        IOptions<BackgroundTaskQueueOptions> options,
        IServiceScopeFactory scopeFactory,
        IQueueMetricsReporter metrics,
        ILogger<BackgroundTaskQueue> logger,
        IHostApplicationLifetime? lifetime = null)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _shutdownToken = lifetime?.ApplicationStopping ?? _completion.Token;
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

        // Start the background process for handling notifications.
        _ = ProcessNotificationsAsync(_shutdownToken);
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
        var queuedTask = new QueuedTask(_queueId, sequence, workItem, DateTime.UtcNow);
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

    private QueueWriteResult TryEnqueueDropWrite(QueueEntry entry)
    {
        lock (_gate)
        {
            if (_isCompleted)
                return RejectWrite(entry);

            if (_count >= _buffer.Length)
                return RejectWrite(entry);

            EnqueueLocked(entry);
        }

        _itemsAvailable.Release();
        RecordEnqueue(entry.Task.SequenceNumber, entry.Task.WorkItem);
        return new QueueWriteResult(QueueWriteResultCode.Enqueued, entry.Task.SequenceNumber);
    }

    private QueueWriteResult TryEnqueueDropOldest(QueueEntry entry)
    {
        QueueEntry? evicted = null;
        var shouldSignal = false;

        lock (_gate)
        {
            if (_isCompleted)
                return RejectWrite(entry);

            if (_count >= _buffer.Length)
            {
                evicted = DequeueLocked();
                if (_metricsEnabled) _metrics.ItemDroppedOldest();
                shouldSignal = false;
            }
            else
            {
                shouldSignal = true;
            }

            EnqueueLocked(entry);
        }

        if (evicted.HasValue)
            evicted.Value.Reject(new ChannelClosedException());

        if (shouldSignal)
            _itemsAvailable.Release();

        RecordEnqueue(entry.Task.SequenceNumber, entry.Task.WorkItem);
        return new QueueWriteResult(QueueWriteResultCode.Enqueued, entry.Task.SequenceNumber);
    }

    private QueueWriteResult TryEnqueueDropNewest(QueueEntry entry)
    {
        QueueEntry? evicted = null;
        var shouldSignal = false;

        lock (_gate)
        {
            if (_isCompleted)
                return RejectWrite(entry);

            if (_count >= _buffer.Length)
            {
                evicted = RemoveNewestLocked();
                if (_metricsEnabled) _metrics.ItemDroppedNewest();
                shouldSignal = false;
            }
            else
            {
                shouldSignal = true;
            }

            EnqueueLocked(entry);
        }

        if (evicted.HasValue)
            evicted.Value.Reject(new ChannelClosedException());

        if (shouldSignal)
            _itemsAvailable.Release();

        RecordEnqueue(entry.Task.SequenceNumber, entry.Task.WorkItem);
        return new QueueWriteResult(QueueWriteResultCode.Enqueued, entry.Task.SequenceNumber);
    }

    private async Task<QueueWriteResult> EnqueueWaitAsync(QueueEntry entry, CancellationToken cancellationToken)
    {
        if (_freeSlots is null)
            throw new InvalidOperationException("Queue was not configured for Wait mode.");

        var waited = !_freeSlots.Wait(0);
        if (waited)
        {
            CancellationTokenSource? linkedCts = null;
            try
            {
                var waitToken = cancellationToken;
                if (cancellationToken.CanBeCanceled)
                {
                    linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _completion.Token);
                    waitToken = linkedCts.Token;
                }
                else
                {
                    waitToken = _completion.Token;
                }

                await _freeSlots.WaitAsync(waitToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return RejectWrite(entry);
            }
            finally
            {
                linkedCts?.Dispose();
            }
        }

        lock (_gate)
        {
            if (_isCompleted)
            {
                _freeSlots.Release();
                return RejectWrite(entry);
            }

            EnqueueLocked(entry);
        }

        _itemsAvailable.Release();
        RecordEnqueue(entry.Task.SequenceNumber, entry.Task.WorkItem);
        return new QueueWriteResult(waited ? QueueWriteResultCode.Waited : QueueWriteResultCode.Enqueued, entry.Task.SequenceNumber);
    }

    private QueueWriteResult RejectWrite(QueueEntry entry)
    {
        entry.Reject(new ChannelClosedException());
        if (_metricsEnabled) _metrics.ItemDroppedNewest();
        PublishNotification(new TaskRejectedNotification(entry.Task.SequenceNumber, _options.FullMode));
        return new QueueWriteResult(QueueWriteResultCode.DroppedNewest, entry.Task.SequenceNumber);
    }

    private ValueTask<QueuedTask> DequeueAsync(CancellationToken cancellationToken)
    {
        return new ValueTask<QueuedTask>(DequeueCoreAsync(cancellationToken));
    }

    private async Task<QueuedTask> DequeueCoreAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await WaitForItemPermitAsync(cancellationToken).ConfigureAwait(false);

            QueueEntry dequeued;
            lock (_gate)
            {
                if (_count <= 0)
                    continue;

                dequeued = DequeueLocked();
            }

            _freeSlots?.Release();

            if (_metricsEnabled)
            {
                _metrics.ItemDequeued();
                _metrics.RecordLatency(DateTime.UtcNow - dequeued.Task.EnqueueTime);
            }

            return dequeued.Task;
        }
    }

    private async ValueTask WaitForItemPermitAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? linkedCts = null;
        try
        {
            var waitToken = cancellationToken;
            if (cancellationToken.CanBeCanceled)
            {
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _completion.Token);
                waitToken = linkedCts.Token;
            }
            else
            {
                waitToken = _completion.Token;
            }

            await _itemsAvailable.WaitAsync(waitToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!_itemsAvailable.Wait(0))
                throw new ChannelClosedException();
        }
        finally
        {
            linkedCts?.Dispose();
        }
    }

    private void Complete()
    {
        lock (_gate)
        {
            if (_isCompleted) return;
            _isCompleted = true;
        }

        _completion.Cancel();
    }

    private void DrainAndCancelPending()
    {
        QueueEntry[] pending;
        int pendingCount;

        lock (_gate)
        {
            pendingCount = _count;
            if (pendingCount == 0) return;

            pending = new QueueEntry[pendingCount];
            for (var i = 0; i < pendingCount; i++)
            {
                pending[i] = _buffer[_head];
                _buffer[_head] = default;
                _head = (_head + 1) % _buffer.Length;
            }

            _count = 0;
            _head = 0;
            _tail = 0;
        }

        while (_itemsAvailable.Wait(0))
        {
        }

        for (var i = 0; i < pendingCount; i++)
            pending[i].Cancel(_shutdownToken);
    }

    private void EnqueueLocked(QueueEntry entry)
    {
        _buffer[_tail] = entry;
        _tail = (_tail + 1) % _buffer.Length;
        _count++;
    }

    private QueueEntry DequeueLocked()
    {
        var entry = _buffer[_head];
        _buffer[_head] = default;
        _head = (_head + 1) % _buffer.Length;
        _count--;
        return entry;
    }

    private QueueEntry RemoveNewestLocked()
    {
        _tail = (_tail - 1 + _buffer.Length) % _buffer.Length;
        var entry = _buffer[_tail];
        _buffer[_tail] = default;
        _count--;
        return entry;
    }

    private void RecordEnqueue(long sequence, Func<CancellationToken, Task> workItem)
    {
        if (_metricsEnabled) _metrics.ItemEnqueued();
        PublishNotification(new TaskEnqueuedNotification(sequence, workItem));
    }

    private void PublishNotification(INotification notification)
    {
        _ = _notificationChannel.Writer
            .WriteAsync(notification, _shutdownToken)
            .AsTask()
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    _logger.LogError(
                        t.Exception,
                        "Failed to enqueue notification for background processing.");
            }, TaskScheduler.Default);
    }

    /// <summary>
    ///     The long-running task that processes notifications from the internal notification channel.
    /// </summary>
    private async Task ProcessNotificationsAsync(CancellationToken token)
    {
        try
        {
            var reader = _notificationChannel.Reader;
            await foreach (var notif in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                await _notifSem.WaitAsync(token).ConfigureAwait(false);
                // Dispatch in a fire-and-forget manner to allow concurrent notification processing.
                _ = DispatchAsync(notif, token);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Notification pump cancelled during shutdown.");
        }
        catch (Exception ex)
        {
            // This is a last-resort catch block. Individual dispatch errors are handled in DispatchAsync.
            _logger.LogCritical(ex, "A fatal and unexpected error occurred in the notification pump.");
        }
    }

    /// <summary>
    ///     Dispatches a single notification to its handlers, with a retry policy for transient failures.
    /// </summary>
    /// <param name="notif">The notification to dispatch.</param>
    /// <param name="token">The cancellation token.</param>
    private async Task DispatchAsync(INotification notif, CancellationToken token)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetService<IDirectNotificationDispatcher>();
        if (dispatcher is null)
        {
            _logger.LogWarning(
                "IDirectNotificationDispatcher is not registered. Background task queue notifications will not be dispatched.");
            return;
        }

        try
        {
            for (var attempt = 1; attempt <= _options.NotificationMaxRetries; attempt++)
                try
                {
                    // Attempt to publish the notification.
                    await PublishNotificationAsync(dispatcher, notif, token).ConfigureAwait(false);
                    return; // On success, exit the method.
                }
                catch (OperationCanceledException)
                {
                    // If cancellation is requested, stop immediately.
                    throw;
                }
                catch (Exception ex)
                {
                    // Check if this was the last attempt.
                    if (attempt == _options.NotificationMaxRetries)
                    {
                        // Last attempt failed. Log as an error and give up.
                        _logger.LogError(ex,
                            "Notification {NotificationType} failed permanently after {MaxAttempts} attempts and will be discarded.",
                            notif.GetType().Name, _options.NotificationMaxRetries);
                        return; // Exit without rethrowing; the error is handled.
                    }

                    // Not the last attempt. Log as a warning and delay before retrying.
                    _logger.LogWarning(ex,
                        "Publish attempt {Attempt} for {NotificationType} failed; retrying in {Delay}ms.",
                        attempt, notif.GetType().Name, _options.NotificationRetryDelay.TotalMilliseconds);
                    await Task.Delay(_options.NotificationRetryDelay, token).ConfigureAwait(false);
                }
        }
        catch (OperationCanceledException)
        {
            // Catches cancellation that happens during the Task.Delay.
            _logger.LogWarning("Notification dispatch for {NotificationType} was canceled during a retry delay.", notif.GetType().Name);
        }
        catch (Exception ex)
        {
            // Safety net for unexpected errors in the dispatch logic itself.
            _logger.LogCritical(ex, "An unexpected error occurred in the notification dispatch loop for {NotificationType}.", notif.GetType().Name);
        }
        finally
        {
            // CRITICAL: Always release the semaphore slot.
            _notifSem.Release();
        }
    }

    private static Task PublishNotificationAsync(
        IDirectNotificationDispatcher dispatcher,
        INotification notification,
        CancellationToken cancellationToken)
    {
        return notification switch
        {
            TaskEnqueuedNotification typed => dispatcher.Publish(typed, cancellationToken),
            TaskRejectedNotification typed => dispatcher.Publish(typed, cancellationToken),
            _ => dispatcher.Publish(notification, cancellationToken)
        };
    }
}
