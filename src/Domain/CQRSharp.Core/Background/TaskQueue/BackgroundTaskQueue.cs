using System.Threading.Channels;
using CQRSharp.Abstractions.Data.Interfaces.Notifications;
using CQRSharp.Core.Background.TaskQueue.Telemetry;
using CQRSharp.Core.Background.TaskQueue.Types;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Notifications.Types;
using CQRSharp.Core.Options;
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
    private readonly Channel<QueuedTask> _channel;
    private readonly IDirectNotificationDispatcher _dispatcher;
    private readonly ILogger<BackgroundTaskQueue> _logger;
    private readonly IQueueMetricsReporter _metrics;
    private readonly Channel<INotification> _notificationChannel;
    private readonly SemaphoreSlim _notifSem;
    private readonly BackgroundTaskQueueOptions _options;
    private readonly bool _metricsEnabled;
    private readonly Guid _queueId = Guid.NewGuid();
    private readonly CountingChannelReader _reader;
    private readonly CancellationToken _shutdownToken;

    private long _sequenceCounter;

    /// <summary>
    ///     Initializes a new instance of the <see cref="BackgroundTaskQueue" /> class.
    /// </summary>
    public BackgroundTaskQueue(
        IOptions<BackgroundTaskQueueOptions> options,
        IDirectNotificationDispatcher dispatcher,
        IHostApplicationLifetime lifetime,
        IQueueMetricsReporter metrics,
        ILogger<BackgroundTaskQueue> logger)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _shutdownToken = lifetime.ApplicationStopping;
        _metricsEnabled = _options.EnableMetrics;

        if (_options.Capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(_options.Capacity), "Capacity must be greater than zero.");

        _channel = Channel.CreateBounded<QueuedTask>(new BoundedChannelOptions(_options.Capacity)
        {
            FullMode = _options.FullMode,
            SingleReader = true, // Optimized for a single consumer
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        _reader = new CountingChannelReader(_channel.Reader, _metrics, _metricsEnabled);

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

        _ = QueueBackgroundWorkItemAsync(Wrapper, cancellationToken);
        return tcs.Task;

        async Task Wrapper(CancellationToken ct)
        {
            try
            {
                await workItem(ct).ConfigureAwait(false);
                tcs.SetResult();
            }
            catch (OperationCanceledException ex)
            {
                tcs.SetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }
    }

    /// <inheritdoc />
    public Task<TResult> EnqueueAsync<TResult>(Func<CancellationToken, Task<TResult>> workItem, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = QueueBackgroundWorkItemAsync(Wrapper, cancellationToken);
        return tcs.Task;

        async Task Wrapper(CancellationToken ct)
        {
            try
            {
                var result = await workItem(ct).ConfigureAwait(false);
                tcs.SetResult(result);
            }
            catch (OperationCanceledException ex)
            {
                tcs.SetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }
    }

    ChannelReader<QueuedTask> IBackgroundTaskQueue.Reader => _reader;

    /// <summary>
    ///     Disposes resources used by the queue, such as completing the channels and disposing the semaphore.
    /// </summary>
    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _notificationChannel.Writer.TryComplete();
        _notifSem.Dispose();
        _metrics.Dispose();
    }

    /// <summary>
    ///     Internal method to queue a work item. It handles different full-mode behaviors and reports metrics.
    /// </summary>
    internal async Task<QueueWriteResult> QueueBackgroundWorkItemAsync(Func<CancellationToken, Task> workItem, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        var sequence = Interlocked.Increment(ref _sequenceCounter);
        var writer = _channel.Writer;
        var queuedTask = new QueuedTask(_queueId, sequence, workItem, DateTime.UtcNow);

        switch (_options.FullMode)
        {
            case BoundedChannelFullMode.DropWrite:
                if (writer.TryWrite(queuedTask))
                {
                    RecordEnqueue(sequence, workItem);
                    return new QueueWriteResult(QueueWriteResultCode.Enqueued, sequence);
                }

                return HandleDropNewest(sequence);

            case BoundedChannelFullMode.Wait:
            case BoundedChannelFullMode.DropOldest:
            case BoundedChannelFullMode.DropNewest:
                // For other modes, we can rely on WriteAsync, which is highly optimized and handles them correctly.
                try
                {
                    if (_options.FullMode == BoundedChannelFullMode.DropOldest && _metricsEnabled && _metrics.CurrentCount >= _options.Capacity)
                        _metrics.ItemDroppedOldest();

                    await writer.WriteAsync(queuedTask, cancellationToken).ConfigureAwait(false);
                    RecordEnqueue(sequence, workItem);

                    // Note: WriteAsync doesn't give a direct result code. We determine it based on context.
                    // For simplicity, we can consider it Enqueued or Waited. Enqueued is sufficient.
                    return new QueueWriteResult(QueueWriteResultCode.Enqueued, sequence);
                }
                catch (ChannelClosedException)
                {
                    return HandleDropNewest(sequence);
                }

            default:
                throw new NotSupportedException($"Unsupported BoundedChannelFullMode: {_options.FullMode}");
        }
    }

    private void RecordEnqueue(long sequence, Func<CancellationToken, Task> workItem)
    {
        if (_metricsEnabled) _metrics.ItemEnqueued();
        PublishNotification(new TaskEnqueuedNotification(sequence, workItem));
    }

    private QueueWriteResult HandleDropNewest(long sequence)
    {
        if (_metricsEnabled) _metrics.ItemDroppedNewest();
        PublishNotification(new TaskRejectedNotification(sequence, _options.FullMode));
        return new QueueWriteResult(QueueWriteResultCode.DroppedNewest, sequence);
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
        try
        {
            for (var attempt = 1; attempt <= _options.NotificationMaxRetries; attempt++)
                try
                {
                    // Attempt to publish the notification.
                    await _dispatcher.Publish(notif, token).ConfigureAwait(false);
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
}
