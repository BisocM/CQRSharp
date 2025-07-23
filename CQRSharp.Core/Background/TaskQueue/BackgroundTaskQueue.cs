using System.Collections.Concurrent;
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
///     A thread safe, bounded queue that executes background work items on a separate consumer.
/// </summary>
internal sealed class BackgroundTaskQueue : IBackgroundTaskQueue, IDisposable
{
    private readonly Channel<QueuedTask> _channel;
    private readonly IDirectNotificationDispatcher _dispatcher;
    private readonly ILogger<BackgroundTaskQueue> _logger;
    private readonly IQueueMetricsReporter _metrics;
    private readonly Channel<INotification> _notificationChannel;

    private readonly SemaphoreSlim _notifSem =
        new(Environment.ProcessorCount, Environment.ProcessorCount);

    private readonly BackgroundTaskQueueOptions _options;
    private readonly Guid _queueId = Guid.NewGuid();
    private readonly CountingChannelReader _reader;
    private readonly CancellationToken _shutdownToken;

    /// <summary>
    ///     Tracks callbacks for tasks that have been enqueued but not yet invoked.
    /// </summary>
    private readonly ConcurrentDictionary<long, QueueTaskCompletionWrapper> _taskCompletions
        = new();

    /// <summary>
    ///     Current work item count, updated atomically on enqueue and dequeue.
    /// </summary>
    private long _currentCount;

    private long _sequenceCounter;

    /// <summary>
    ///     Initializes a new instance of <see cref="BackgroundTaskQueue" />.
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

        if (_options.Capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(_options.Capacity),
                "Capacity must be greater than zero.");

        //Primary work channel
        _channel = Channel.CreateBounded<QueuedTask>(new BoundedChannelOptions(_options.Capacity)
        {
            FullMode = _options.FullMode,
            SingleReader = true, //The custom BackgroundTaskQueueConsumer is the only thing that ever reads here. So settings this to true will clear up some internal locks.
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        //Wrap reader so we can maintain count and latency
        _reader = new CountingChannelReader(_channel.Reader, this);

        //Notification channel for enqueue/reject events
        _notificationChannel = Channel.CreateBounded<INotification>(
            new BoundedChannelOptions(_options.CallbackChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

        if (_options.EnableMetrics)
            _logger.LogInformation(
                "BackgroundTaskQueue metrics enabled (capacity = {Capacity}).",
                _options.Capacity);

        //Start dispatch loop for notifications
        _ = ProcessNotificationsAsync(_shutdownToken);
    }

    /// <inheritdoc />
    public ChannelReader<QueuedTask> Reader => _reader;

    /// <inheritdoc />
    public async Task<QueueWriteResult> QueueBackgroundWorkItemAsync(
        Func<CancellationToken, Task> workItem,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        var sequence = Interlocked.Increment(ref _sequenceCounter);
        var queuedTask = new QueuedTask(_queueId, sequence, workItem, DateTime.UtcNow);
        var writer = _channel.Writer;
        QueueWriteResultCode resultCode;

        //If shutdown has been requested, then we just deny this.
        if (_shutdownToken.IsCancellationRequested)
            return new QueueWriteResult(QueueWriteResultCode.DroppedNewest, sequence);

        if (writer.TryWrite(queuedTask))
        {
            RecordEnqueue(sequence, workItem);
            resultCode = QueueWriteResultCode.Enqueued;
        }
        else
        {
            switch (_options.FullMode)
            {
                case BoundedChannelFullMode.DropNewest:
                case BoundedChannelFullMode.DropWrite:
                    _metrics.ItemDroppedNewest();
                    PublishNotification(new TaskRejectedNotification(sequence, _options.FullMode));
                    resultCode = QueueWriteResultCode.DroppedNewest;
                    break;

                case BoundedChannelFullMode.Wait:
                case BoundedChannelFullMode.DropOldest:
                default:
                    await writer.WriteAsync(queuedTask, cancellationToken)
                        .ConfigureAwait(false);
                    RecordEnqueue(sequence, workItem);
                    resultCode = QueueWriteResultCode.Waited;
                    break;
            }
        }

        return new QueueWriteResult(resultCode, sequence);
    }

    /// <inheritdoc />
    public ValueTask<QueuedTask> DequeueAsync(CancellationToken cancellationToken) =>
        _reader.ReadAsync(cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        //This makes readers wake up immediately and lets the notification pump finish up cleanly.
        //Otherwise, we can also make this IAsyncDisposable
        _channel.Writer.Complete();
        _notificationChannel.Writer.Complete();

        _notifSem.Dispose();
        _metrics.Dispose();
    }

    /// <summary>
    ///     Registers callbacks to invoke if a queued task is cancelled or faults.
    /// </summary>
    internal void RegisterTaskCompletion(
        long sequence,
        Action cancel,
        Action<Exception> fault) =>
        _taskCompletions[sequence] = new QueueTaskCompletionWrapper(cancel, fault);

    /// <summary>
    ///     Invokes the successful completion callback for the given sequence.
    /// </summary>
    internal void CompleteTaskAsRan(long sequence) =>
        _taskCompletions.TryRemove(sequence, out _);

    /// <summary>
    ///     Invokes the fault callback for the given sequence.
    /// </summary>
    internal void SignalTaskException(long sequence, Exception ex)
    {
        if (_taskCompletions.TryRemove(sequence, out var wrapper)) wrapper.ExceptionAction(ex);
    }

    /// <summary>
    ///     Atomically decrements the queue count and records time spent in queue.
    /// </summary>
    internal void DecrementCountAndRecordLatency(QueuedTask task)
    {
        var latency = DateTime.UtcNow - task.EnqueueTime; //TODO: DateTime is fine and all, but it is a bit too coarse. Maybe use Stopwatch.
        _metrics.RecordLatency(latency);
        Interlocked.Decrement(ref _currentCount);
    }

    private void RecordEnqueue(long sequence, Func<CancellationToken, Task> workItem)
    {
        _metrics.ItemEnqueued();
        Interlocked.Increment(ref _currentCount);
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
                        t.Exception!,
                        "Failed to enqueue notification.");
            }, TaskScheduler.Default);
    }

    private async Task ProcessNotificationsAsync(CancellationToken token)
    {
        try
        {
            var reader = _notificationChannel.Reader;
            while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
            while (reader.TryRead(out var notif))
            {
                await _notifSem.WaitAsync(token).ConfigureAwait(false);
                _ = DispatchAsync(notif, token);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "Notification pump cancelled during shutdown.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in notification pump.");
        }
    }

    private async Task DispatchAsync(INotification notif, CancellationToken token)
    {
        try
        {
            for (var attempt = 1;; attempt++)
                try
                {
                    await _dispatcher.Publish(notif, token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (attempt < _options.NotificationMaxRetries)
                {
                    _logger.LogWarning(
                        ex,
                        "Publish attempt {Attempt} for {NotificationType} failed; retrying.",
                        attempt,
                        notif.GetType().Name);
                    await Task.Delay(
                            _options.NotificationRetryDelay,
                            token)
                        .ConfigureAwait(false);
                }
        }
        finally
        {
            _notifSem.Release();
        }
    }
}