using System.Collections.Concurrent;
using System.Threading.Channels;
using CQRSharp.Core.BackgroundTasks.Types;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Notifications.Types;
using CQRSharp.Core.Options;
using CQRSharp.Shared.Data.Interfaces.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.BackgroundTasks
{
    /// <summary>
    /// A thread-safe bounded queue for background work items.
    /// Supports DropNewest, DropOldest, and Wait policies when full, publishes
    /// notifications for enqueue/reject events with retry logic, and optionally
    /// emits basic metrics.
    /// </summary>
    public sealed class BackgroundTaskQueue : IBackgroundTaskQueue
    {
        private static readonly ConcurrentDictionary<long, QueueTaskCompletionWrapper> TaskCompletions =
            new();

        internal long CurrentCount;
        
        private readonly Channel<QueuedTask> _channel;
        private readonly CountingChannelReader _reader;
        private readonly Channel<INotification> _notificationChannel;
        private readonly SemaphoreSlim _notificationDispatchSemaphore;
        private readonly BackgroundTaskQueueOptions _options;
        private readonly INotificationDispatcher _dispatcher;
        private readonly ILogger<BackgroundTaskQueue> _logger;
        private readonly CancellationToken _shutdownToken;

        private long _sequenceCounter;
        private long _enqueuedCount;
        private long _droppedNewestCount;
        private long _droppedOldestCount;

        /// <inheritdoc/>
        public long TotalItemsEnqueued => Interlocked.Read(ref _enqueuedCount);

        /// <inheritdoc/>
        public long TotalDroppedNewest  => Interlocked.Read(ref _droppedNewestCount);

        /// <inheritdoc/>
        public long TotalDroppedOldest  => Interlocked.Read(ref _droppedOldestCount);

        /// <inheritdoc/>
        public ChannelReader<QueuedTask> Reader => _reader;

        /// <summary>
        /// Constructs a new <see cref="BackgroundTaskQueue"/>.
        /// </summary>
        /// <param name="options">Queue capacity, full‑mode, retry, metrics and shutdown settings.</param>
        /// <param name="dispatcher">Dispatcher for sending enqueue/reject notifications.</param>
        /// <param name="lifetime">Application lifetime, for shutdown notification.</param>
        /// <param name="logger">Logger for internal events, errors, and (optionally) metrics.</param>
        public BackgroundTaskQueue(
            IOptions<BackgroundTaskQueueOptions> options,
            INotificationDispatcher dispatcher,
            IHostApplicationLifetime lifetime,
            ILogger<BackgroundTaskQueue> logger)
        {
            _options       = options.Value ?? throw new ArgumentNullException(nameof(options));
            _dispatcher    = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _logger        = logger ?? throw new ArgumentNullException(nameof(logger));
            _shutdownToken = lifetime.ApplicationStopping;

            if (_options.Capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(_options.Capacity), "Capacity must be > 0.");

            //Initialize the bounded work-item channel
            var channelOptions = new BoundedChannelOptions(_options.Capacity)
            {
                FullMode                      = _options.FullMode,
                SingleReader                  = false,
                SingleWriter                  = false,
                AllowSynchronousContinuations = false
            };
            _channel = Channel.CreateBounded<QueuedTask>(channelOptions);
            _reader  = new CountingChannelReader(_channel.Reader, this);

            //Initialize the notification channel
            var notifOptions = new BoundedChannelOptions(_options.CallbackChannelCapacity)
            {
                FullMode                      = BoundedChannelFullMode.DropOldest,
                SingleReader                  = true,
                SingleWriter                  = false,
                AllowSynchronousContinuations = false
            };
            _notificationChannel = Channel.CreateBounded<INotification>(notifOptions);

            //Throttle concurrent publishes to at most ProcessorCount
            _notificationDispatchSemaphore = new SemaphoreSlim(Environment.ProcessorCount);

            if (_options.EnableMetrics)
            {
                _logger.LogInformation("BackgroundTaskQueue: metrics ENABLED (capacity={Capacity}).", _options.Capacity);
            }

            //Kick off the background notification loop
            _ = ProcessNotificationsAsync(_shutdownToken);
        }

        /// <inheritdoc/>
        public async Task<QueueWriteResult> QueueBackgroundWorkItemAsync(
            Func<CancellationToken, Task> workItem,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(workItem);

            long seq = Interlocked.Increment(ref _sequenceCounter);
            var qt   = new QueuedTask(seq, workItem);
            var writer = _channel.Writer;

            QueueWriteResult result;
            switch (_options.FullMode)
            {
                case BoundedChannelFullMode.DropNewest:
                    if (!writer.TryWrite(qt))
                    {
                        Interlocked.Increment(ref _droppedNewestCount);
                        EnqueueNotification(new TaskRejectedNotification(seq, _options.FullMode));
                        result = new QueueWriteResult(QueueWriteResultCode.DroppedNewest, seq);
                    }
                    else
                    {
                        Interlocked.Increment(ref _enqueuedCount);
                        Interlocked.Increment(ref CurrentCount);
                        EnqueueNotification(new TaskEnqueuedNotification(seq, workItem));
                        result = new QueueWriteResult(QueueWriteResultCode.Enqueued, seq);
                    }
                    break;

                case BoundedChannelFullMode.DropOldest:
                    if (Interlocked.Read(ref CurrentCount) >= _options.Capacity &&
                        _reader.TryRead(out var droppedTask))
                    {
                        Interlocked.Increment(ref _droppedOldestCount);
                        if (TaskCompletions.TryRemove(droppedTask.SequenceNumber, out var wrapper))
                            wrapper.CancelAction();
                        EnqueueNotification(new TaskRejectedNotification(droppedTask.SequenceNumber, _options.FullMode));
                    }
                    await writer.WriteAsync(qt, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _enqueuedCount);
                    Interlocked.Increment(ref CurrentCount);
                    EnqueueNotification(new TaskEnqueuedNotification(seq, workItem));
                    result = new QueueWriteResult(QueueWriteResultCode.DroppedOldest, seq);
                    break;

                case BoundedChannelFullMode.Wait:
                case BoundedChannelFullMode.DropWrite:
                default:
                    if (writer.TryWrite(qt))
                    {
                        Interlocked.Increment(ref _enqueuedCount);
                        Interlocked.Increment(ref CurrentCount);
                        EnqueueNotification(new TaskEnqueuedNotification(seq, workItem));
                        result = new QueueWriteResult(QueueWriteResultCode.Enqueued, seq);
                    }
                    else
                    {
                        await writer.WriteAsync(qt, cancellationToken).ConfigureAwait(false);
                        Interlocked.Increment(ref _enqueuedCount);
                        Interlocked.Increment(ref CurrentCount);
                        EnqueueNotification(new TaskEnqueuedNotification(seq, workItem));
                        result = new QueueWriteResult(QueueWriteResultCode.Waited, seq);
                    }
                    break;
            }

            if (_options.EnableMetrics)
            {
                _logger.LogInformation(
                    "Metrics: Enqueued={Enq}, DroppedNewest={DN}, DroppedOldest={DO}, CurrentLen={Len}",
                    TotalItemsEnqueued,
                    TotalDroppedNewest,
                    TotalDroppedOldest,
                    Interlocked.Read(ref CurrentCount));
            }

            return result;
        }

        /// <inheritdoc/>
        public ValueTask<QueuedTask> DequeueAsync(CancellationToken cancellationToken) =>
            _reader.ReadAsync(cancellationToken);

        private void EnqueueNotification(INotification notification)
        {
            var writeTask = WriteNotificationAsync(notification);
            writeTask.ContinueWith(t =>
            {
                if (t is { IsFaulted: true, Exception: not null })
                {
                    _logger.LogError(
                        t.Exception,
                        "Failed to enqueue notification of type {NotificationType}.",
                        notification.GetType().Name);
                }
            }, TaskScheduler.Default);
        }

        private async Task WriteNotificationAsync(INotification notification)
        {
            try
            {
                await _notificationChannel.Writer
                    .WriteAsync(notification, _shutdownToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Notification enqueue canceled due to shutdown.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while enqueueing notification.");
            }
        }

        private async Task ProcessNotificationsAsync(CancellationToken cancellationToken)
        {
            var reader = _notificationChannel.Reader;
            try
            {
                while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var notification))
                    {
                        await _notificationDispatchSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                int attempt = 0;
                                while (true)
                                {
                                    try
                                    {
                                        await _dispatcher
                                            .Publish(notification, CancellationToken.None)
                                            .ConfigureAwait(false);
                                        return;
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        throw;
                                    }
                                    catch (Exception ex)
                                    {
                                        attempt++;
                                        _logger.LogWarning(
                                            ex,
                                            "Publish attempt {Attempt} for {Notification} failed.",
                                            attempt,
                                            notification.GetType().Name);

                                        if (attempt < _options.NotificationMaxRetries)
                                        {
                                            await Task
                                                .Delay(_options.NotificationRetryDelay, cancellationToken)
                                                .ConfigureAwait(false);
                                            continue;
                                        }

                                        _logger.LogError(
                                            ex,
                                            "Notification {Notification} permanently failed after {MaxAttempts} attempts.",
                                            notification.GetType().Name,
                                            _options.NotificationMaxRetries);
                                        return;
                                    }
                                }
                            }
                            finally
                            {
                                _notificationDispatchSemaphore.Release();
                            }
                        }, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Notification processing canceled due to shutdown.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in notification processing loop.");
            }
        }

        /// <summary>
        /// Registers a TaskCompletionSource wrapper so the caller can be canceled
        /// or faulted if the task is dropped.
        /// </summary>
        internal void RegisterTaskCompletion(long sequence, Action cancelAction, Action<Exception> exceptionAction) =>
            TaskCompletions[sequence] = new QueueTaskCompletionWrapper(cancelAction, exceptionAction);

        /// <summary>
        /// Signals that a task has completed normally and removes its completion tracking.
        /// </summary>
        internal static void CompleteTaskAsRan(long sequence) =>
            TaskCompletions.TryRemove(sequence, out _);

        /// <summary>
        /// Attempts to signal a task fault to its registered completion wrapper.
        /// </summary>
        internal static void SignalTaskException(long sequence, Exception ex)
        {
            if (TaskCompletions.TryRemove(sequence, out var wrapper))
                wrapper.ExceptionAction(ex);
        }
    }
}