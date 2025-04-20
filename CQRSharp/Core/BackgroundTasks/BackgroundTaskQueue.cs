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
    /// Supports DropNewest, DropOldest, and Wait policies when full, and publishes notifications
    /// for task enqueue/reject events, with retry logic.
    /// </summary>
    public sealed class BackgroundTaskQueue : IBackgroundTaskQueue
    {
        /// <summary>
        /// Wraps a background task's completion so it can be signaled if the task is dropped
        /// or faults before execution.
        /// </summary>
        private class QueueTaskCompletionWrapper(Action cancelAction, Action<Exception> exceptionAction)
        {
            /// <summary>Action to invoke to cancel the awaiting TaskCompletionSource.</summary>
            public Action CancelAction { get; } = cancelAction;

            /// <summary>Action to invoke to fault the awaiting TaskCompletionSource with an exception.</summary>
            public Action<Exception> ExceptionAction { get; } = exceptionAction;
        }

        //Tracks pending tasks by sequence number for cancellation or faulting.
        private static readonly ConcurrentDictionary<long, QueueTaskCompletionWrapper> TaskCompletions = new();

        private readonly Channel<QueuedTask>      _channel;
        private readonly CountingChannelReader    _reader;
        private readonly Channel<INotification>   _notificationChannel;
        private readonly SemaphoreSlim            _notificationDispatchSemaphore;
        private readonly BackgroundTaskQueueOptions _options;
        private readonly INotificationDispatcher  _dispatcher;
        private readonly ILogger<BackgroundTaskQueue> _logger;
        private readonly CancellationToken        _shutdownToken;

        private long _sequenceCounter;
        private long _enqueuedCount;
        private long _droppedNewestCount;
        private long _droppedOldestCount;
        private long _currentCount;

        private const int NotificationMaxRetries = 3;
        private static readonly TimeSpan NotificationRetryDelay = TimeSpan.FromSeconds(2);

        /// <inheritdoc/>
        public long TotalItemsEnqueued => Interlocked.Read(ref _enqueuedCount);

        /// <inheritdoc/>
        public long TotalDroppedNewest  => Interlocked.Read(ref _droppedNewestCount);

        /// <inheritdoc/>
        public long TotalDroppedOldest  => Interlocked.Read(ref _droppedOldestCount);

        /// <inheritdoc/>
        public ChannelReader<QueuedTask> Reader => _reader;

        /// <summary>
        /// Constructs a new BackgroundTaskQueue.
        /// </summary>
        /// <param name="options">Queue capacity and full‑mode settings.</param>
        /// <param name="dispatcher">Dispatcher for sending notifications.</param>
        /// <param name="lifetime">Application lifetime, for shutdown notification.</param>
        /// <param name="logger">Logger for internal errors and warnings.</param>
        public BackgroundTaskQueue(
            IOptions<BackgroundTaskQueueOptions> options,
            INotificationDispatcher dispatcher,
            IHostApplicationLifetime lifetime,
            ILogger<BackgroundTaskQueue> logger)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(dispatcher);
            ArgumentNullException.ThrowIfNull(lifetime);
            ArgumentNullException.ThrowIfNull(logger);

            _options        = options.Value;
            _dispatcher     = dispatcher;
            _logger         = logger;
            _shutdownToken  = lifetime.ApplicationStopping;

            if (_options.Capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(_options.Capacity), "Capacity must be greater than zero.");

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

            //Initialize a bounded channel for notifications
            var notifOptions = new BoundedChannelOptions(_options.CallbackChannelCapacity)
            {
                FullMode                      = BoundedChannelFullMode.Wait,
                SingleReader                  = true,
                SingleWriter                  = false,
                AllowSynchronousContinuations = false
            };
            _notificationChannel = Channel.CreateBounded<INotification>(notifOptions);

            //Throttle concurrent publishes to at most ProcessorCount at once
            _notificationDispatchSemaphore = new SemaphoreSlim(Environment.ProcessorCount);

            //Start the background notification dispatch loop
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

            switch (_options.FullMode)
            {
                case BoundedChannelFullMode.DropNewest:
                case BoundedChannelFullMode.DropWrite:
                    //Non‑blocking enqueue; drop if full
                    if (!writer.TryWrite(qt))
                    {
                        Interlocked.Increment(ref _droppedNewestCount);
                        EnqueueNotification(new TaskRejectedNotification(seq, _options.FullMode));
                        return new QueueWriteResult(QueueWriteResultCode.DroppedNewest, seq);
                    }

                    Interlocked.Increment(ref _enqueuedCount);
                    Interlocked.Increment(ref _currentCount);
                    EnqueueNotification(new TaskEnqueuedNotification(seq, workItem));
                    return new QueueWriteResult(QueueWriteResultCode.Enqueued, seq);

                case BoundedChannelFullMode.DropOldest:
                    //If at capacity, remove the oldest
                    if (Interlocked.Read(ref _currentCount) >= _options.Capacity &&
                        _reader.TryRead(out var droppedTask))
                    {
                        Interlocked.Increment(ref _droppedOldestCount);
                        if (TaskCompletions.TryRemove(droppedTask.SequenceNumber, out var wrapper))
                            wrapper.CancelAction();
                        EnqueueNotification(new TaskRejectedNotification(droppedTask.SequenceNumber, _options.FullMode));
                    }

                    //Then enqueue (will wait if still full)
                    await writer.WriteAsync(qt, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _enqueuedCount);
                    Interlocked.Increment(ref _currentCount);
                    EnqueueNotification(new TaskEnqueuedNotification(seq, workItem));
                    return new QueueWriteResult(QueueWriteResultCode.DroppedOldest, seq);

                case BoundedChannelFullMode.Wait:
                default:
                    //Try a fast non‑blocking write first
                    if (writer.TryWrite(qt))
                    {
                        Interlocked.Increment(ref _enqueuedCount);
                        Interlocked.Increment(ref _currentCount);
                        EnqueueNotification(new TaskEnqueuedNotification(seq, workItem));
                        return new QueueWriteResult(QueueWriteResultCode.Enqueued, seq);
                    }

                    //Otherwise wait until space is free
                    await writer.WriteAsync(qt, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _enqueuedCount);
                    Interlocked.Increment(ref _currentCount);
                    EnqueueNotification(new TaskEnqueuedNotification(seq, workItem));
                    return new QueueWriteResult(QueueWriteResultCode.Waited, seq);
            }
        }

        /// <inheritdoc/>
        public ValueTask<QueuedTask> DequeueAsync(CancellationToken cancellationToken) =>
            _reader.ReadAsync(cancellationToken);

        /// <summary>
        /// Fire‑and‑forget enqueue of a notification, logging any enqueue failure.
        /// </summary>
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

        /// <summary>
        /// Writes a notification into the notification channel, observing shutdown.
        /// </summary>
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
                _logger.LogWarning("Notification enqueue canceled due to application shutdown.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while enqueueing notification.");
            }
        }

        /// <summary>
        /// Background loop that reads notifications and publishes them with bounded concurrency and retries.
        /// </summary>
        private async Task ProcessNotificationsAsync(CancellationToken cancellationToken)
        {
            var reader = _notificationChannel.Reader;

            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var notification))
                {
                    //Throttle concurrent Publish calls
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
                                    //Now Publish throws if any handler fails
                                    await _dispatcher
                                        .Publish(notification, CancellationToken.None)
                                        .ConfigureAwait(false);
                                    return;
                                }
                                catch (OperationCanceledException)
                                {
                                    //Respect shutdown token
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

                                    if (attempt < NotificationMaxRetries)
                                    {
                                        await Task
                                            .Delay(NotificationRetryDelay, cancellationToken)
                                            .ConfigureAwait(false);
                                        continue;
                                    }

                                    //Final failure: log and swallow
                                    _logger.LogError(
                                        ex,
                                        "Notification {Notification} permanently failed after {MaxAttempts} attempts.",
                                        notification.GetType().Name,
                                        NotificationMaxRetries);
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

        #region CountingChannelReader

        /// <summary>
        /// Wraps the channel reader to decrement the current count whenever an item is dequeued.
        /// </summary>
        private sealed class CountingChannelReader(ChannelReader<QueuedTask> inner, BackgroundTaskQueue parent)
            : ChannelReader<QueuedTask>
        {
            public override bool TryRead(out QueuedTask item)
            {
                var result = inner.TryRead(out item);
                if (result)
                    Interlocked.Decrement(ref parent._currentCount);
                return result;
            }

            public override async ValueTask<QueuedTask> ReadAsync(CancellationToken cancellationToken = default)
            {
                var item = await inner.ReadAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Decrement(ref parent._currentCount);
                return item;
            }

            public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
                inner.WaitToReadAsync(cancellationToken);
        }

        #endregion

        /// <summary>
        /// Registers a TaskCompletionSource wrapper so the caller can be canceled or faulted if the task is dropped.
        /// </summary>
        /// <param name="sequence">Unique sequence number of the work item.</param>
        /// <param name="cancelAction">Action to cancel the awaiting task.</param>
        /// <param name="exceptionAction">Action to fault the awaiting task with an exception.</param>
        internal void RegisterTaskCompletion(long sequence, Action cancelAction, Action<Exception> exceptionAction) => TaskCompletions[sequence] = new QueueTaskCompletionWrapper(cancelAction, exceptionAction);

        /// <summary>
        /// Signals that a task has completed normally and removes its completion tracking.
        /// </summary>
        /// <param name="sequence">Sequence number of the completed work item.</param>
        internal static void CompleteTaskAsRan(long sequence) => TaskCompletions.TryRemove(sequence, out _);
    }
}