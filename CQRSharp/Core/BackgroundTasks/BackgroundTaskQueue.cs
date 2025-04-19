using System.Threading.Channels;
using Microsoft.Extensions.Options;
using CQRSharp.Core.BackgroundTasks.Types;
using CQRSharp.Core.Options;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Notifications.Types;
using CQRSharp.Shared.Data.Interfaces.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.BackgroundTasks
{
    /// <summary>
    ///     A bounded, high-throughput background task queue that offloads
    ///     task-enqueued/rejected notifications to a dedicated background loop.
    /// </summary>
    public sealed class BackgroundTaskQueue : IBackgroundTaskQueue
    {
        private readonly Channel<QueuedTask> _channel;
        private readonly CountingChannelReader _reader;
        private readonly BackgroundTaskQueueOptions _options;
        private readonly Channel<INotification> _notificationChannel;
        private readonly INotificationDispatcher _dispatcher;
        private readonly ILogger<BackgroundTaskQueue> _logger;
        private readonly CancellationToken _shutdownToken;

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
        public long TotalDroppedNewest => Interlocked.Read(ref _droppedNewestCount);

        /// <inheritdoc/>
        public long TotalDroppedOldest => Interlocked.Read(ref _droppedOldestCount);

        /// <inheritdoc/>
        public ChannelReader<QueuedTask> Reader => _reader;

        /// <summary>
        ///     Initializes a new instance of the <see cref="BackgroundTaskQueue"/> class.
        /// </summary>
        /// <param name="options">Configuration options for the queue.</param>
        /// <param name="dispatcher">Notification dispatcher for enqueue/reject events.</param>
        /// <param name="lifetime">Host lifetime to observe shutdown.</param>
        /// <param name="logger">Logger instance.</param>
        public BackgroundTaskQueue(
            IOptions<BackgroundTaskQueueOptions> options,
            INotificationDispatcher dispatcher,
            IHostApplicationLifetime lifetime,
            ILogger<BackgroundTaskQueue> logger)
        {
            ArgumentNullException.ThrowIfNull(dispatcher);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(lifetime);

            _options       = options.Value;
            _dispatcher    = dispatcher;
            _logger        = logger;
            _shutdownToken = lifetime.ApplicationStopping;

            if (_options.Capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(_options.Capacity), "Capacity must be > 0.");

            var bounded = new BoundedChannelOptions(_options.Capacity)
            {
                FullMode                     = _options.FullMode,
                SingleReader                 = false,
                SingleWriter                 = false,
                AllowSynchronousContinuations = false
            };
            _channel = Channel.CreateBounded<QueuedTask>(bounded);
            _reader  = new CountingChannelReader(_channel.Reader, this);

            var notifOptions = new BoundedChannelOptions(_options.CallbackChannelCapacity)
            {
                FullMode                     = BoundedChannelFullMode.Wait,
                SingleReader                 = true,
                SingleWriter                 = false,
                AllowSynchronousContinuations = false
            };
            _notificationChannel = Channel.CreateBounded<INotification>(notifOptions);

            // Start the notification loop and observe shutdown
            _ = ProcessNotificationsAsync(_shutdownToken);
        }

        /// <inheritdoc/>
        public async Task<QueueWriteResult> QueueBackgroundWorkItemAsync(
            Func<CancellationToken, Task> workItem,
            CancellationToken cancellationToken)
        {
            if (workItem is null)
                throw new ArgumentNullException(nameof(workItem));

            var seq    = Interlocked.Increment(ref _sequenceCounter);
            var qt     = new QueuedTask(seq, workItem);
            var writer = _channel.Writer;

            switch (_options.FullMode)
            {
                case BoundedChannelFullMode.DropNewest:
                case BoundedChannelFullMode.DropWrite:
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
                    if (Interlocked.Read(ref _currentCount) >= _options.Capacity)
                    {
                        if (_reader.TryRead(out _))
                            Interlocked.Increment(ref _droppedOldestCount);
                    }
                    await writer.WriteAsync(qt, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _enqueuedCount);
                    Interlocked.Increment(ref _currentCount);
                    EnqueueNotification(new TaskEnqueuedNotification(seq, workItem));
                    return new QueueWriteResult(QueueWriteResultCode.DroppedOldest, seq);

                case BoundedChannelFullMode.Wait:
                default:
                    await writer.WriteAsync(qt, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _enqueuedCount);
                    Interlocked.Increment(ref _currentCount);
                    EnqueueNotification(new TaskEnqueuedNotification(seq, workItem));
                    return new QueueWriteResult(QueueWriteResultCode.Enqueued, seq);
            }
        }

        /// <inheritdoc/>
        public ValueTask<QueuedTask> DequeueAsync(CancellationToken cancellationToken) =>
            _reader.ReadAsync(cancellationToken);

        /// <summary>
        ///     Queues a notification for background dispatch, retrying once if needed.
        /// </summary>
        /// <param name="notification">Notification to enqueue.</param>
        private void EnqueueNotification(INotification notification)
        {
            // Fire-and-forget to avoid blocking the enqueue path
            _ = WriteNotificationAsync(notification);
        }

        /// <summary>
        ///     Writes a notification into the notification channel, observing shutdown.
        /// </summary>
        /// <param name="notification">The notification to write.</param>
        private async Task WriteNotificationAsync(INotification notification)
        {
            try
            {
                await _notificationChannel
                    .Writer
                    .WriteAsync(notification, _shutdownToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Notification write cancelled due to application shutdown.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enqueue notification.");
            }
        }

        /// <summary>
        ///     Drains notifications and publishes them with retries and logging.
        /// </summary>
        /// <param name="cancellationToken">Token that signals host shutdown.</param>
        private async Task ProcessNotificationsAsync(CancellationToken cancellationToken)
        {
            var reader = _notificationChannel.Reader;
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var notification))
                {
                    int attempt = 0;
                    while (attempt < NotificationMaxRetries)
                    {
                        try
                        {
                            await _dispatcher.Publish(
                                notification,
                                CancellationToken.None)
                                .ConfigureAwait(false);
                            break;
                        }
                        catch (Exception ex)
                        {
                            attempt++;
                            _logger.LogWarning(
                                ex,
                                "Failed to publish notification (attempt {Attempt})",
                                attempt);
                            if (attempt < NotificationMaxRetries)
                                await Task.Delay(NotificationRetryDelay, cancellationToken)
                                      .ConfigureAwait(false);
                            else
                                _logger.LogError(
                                    ex,
                                    "Permanently failed to publish notification after {MaxAttempts} attempts",
                                    NotificationMaxRetries);
                        }
                    }
                }
            }
        }

        #region CountingChannelReader
        private sealed class CountingChannelReader : ChannelReader<QueuedTask>
        {
            private readonly ChannelReader<QueuedTask> _inner;
            private readonly BackgroundTaskQueue _parent;

            public CountingChannelReader(
                ChannelReader<QueuedTask> inner,
                BackgroundTaskQueue parent)
            {
                _inner  = inner;
                _parent = parent;
            }

            public override bool TryRead(out QueuedTask item)
            {
                var result = _inner.TryRead(out item);
                if (result)
                    Interlocked.Decrement(ref _parent._currentCount);
                return result;
            }

            public override async ValueTask<QueuedTask> ReadAsync(
                CancellationToken cancellationToken = default)
            {
                var item = await _inner
                    .ReadAsync(cancellationToken)
                    .ConfigureAwait(false);
                Interlocked.Decrement(ref _parent._currentCount);
                return item;
            }

            public override IAsyncEnumerable<QueuedTask> ReadAllAsync(
                CancellationToken cancellationToken = default) =>
                _inner.ReadAllAsync(cancellationToken);

            public override ValueTask<bool> WaitToReadAsync(
                CancellationToken cancellationToken = default) =>
                _inner.WaitToReadAsync(cancellationToken);
        }
        #endregion
    }
}