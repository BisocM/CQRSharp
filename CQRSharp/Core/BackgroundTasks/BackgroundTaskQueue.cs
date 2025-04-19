using System.Threading.Channels;
using Microsoft.Extensions.Options;
using CQRSharp.Core.BackgroundTasks.Types;
using CQRSharp.Core.Options;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Notifications.Types;
using CQRSharp.Shared.Data.Interfaces.Notifications;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.BackgroundTasks
{
    /// <summary>
    ///     A bounded, high-throughput background task queue that offloads
    ///     task-enqueued/rejected notifications to a dedicated background loop.
    /// </summary>
    /// <remarks>
    ///     Uses a <see cref="Channel{QueuedTask}"/> internally to store work
    ///     items in FIFO order, and a separate <see cref="Channel{INotification}"/>
    ///     to decouple notification dispatch from the enqueue path.
    ///     Notifications are published via <see cref="INotificationDispatcher"/>.
    /// </remarks>
    public sealed class BackgroundTaskQueue : IBackgroundTaskQueue
    {
        private readonly Channel<QueuedTask> _channel;
        private readonly CountingChannelReader _reader;
        private readonly BackgroundTaskQueueOptions _options;
        private readonly Channel<INotification> _notificationChannel;
        private readonly INotificationDispatcher _dispatcher;
        private readonly ILogger<BackgroundTaskQueue> _logger;

        private const int NotificationMaxRetries = 3;
        private readonly TimeSpan _notificationRetryDelay = TimeSpan.FromSeconds(2);

        private long _sequenceCounter;
        private long _enqueuedCount;
        private long _droppedNewestCount;
        private long _droppedOldestCount;
        private long _currentCount;

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
        public BackgroundTaskQueue(
            IOptions<BackgroundTaskQueueOptions> options,
            INotificationDispatcher dispatcher,
            ILogger<BackgroundTaskQueue> logger)
        {
            ArgumentNullException.ThrowIfNull(dispatcher);
            ArgumentNullException.ThrowIfNull(logger);

            _options    = options.Value;
            _dispatcher = dispatcher;
            _logger     = logger;

            if (_options.Capacity <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(_options.Capacity), "Capacity must be > 0.");

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

            _ = ProcessNotificationsAsync(CancellationToken.None);
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
                        EnqueueNotification(
                            new TaskRejectedNotification(seq, _options.FullMode));
                        return new QueueWriteResult(
                            QueueWriteResultCode.DroppedNewest, seq);
                    }
                    Interlocked.Increment(ref _enqueuedCount);
                    Interlocked.Increment(ref _currentCount);
                    EnqueueNotification(
                        new TaskEnqueuedNotification(seq, workItem));
                    return new QueueWriteResult(
                        QueueWriteResultCode.Enqueued, seq);

                case BoundedChannelFullMode.DropOldest:
                    if (Interlocked.Read(ref _currentCount) >= _options.Capacity)
                    {
                        if (_reader.TryRead(out _))
                            Interlocked.Increment(ref _droppedOldestCount);
                    }
                    await writer.WriteAsync(qt, cancellationToken)
                                .ConfigureAwait(false);
                    Interlocked.Increment(ref _enqueuedCount);
                    Interlocked.Increment(ref _currentCount);
                    EnqueueNotification(
                        new TaskEnqueuedNotification(seq, workItem));
                    return new QueueWriteResult(
                        QueueWriteResultCode.DroppedOldest, seq);

                case BoundedChannelFullMode.Wait:
                default:
                    await writer.WriteAsync(qt, cancellationToken)
                                .ConfigureAwait(false);
                    Interlocked.Increment(ref _enqueuedCount);
                    Interlocked.Increment(ref _currentCount);
                    EnqueueNotification(
                        new TaskEnqueuedNotification(seq, workItem));
                    return new QueueWriteResult(
                        QueueWriteResultCode.Enqueued, seq);
            }
        }

        /// <inheritdoc/>
        public ValueTask<QueuedTask> DequeueAsync(CancellationToken cancellationToken) =>
            _reader.ReadAsync(cancellationToken);

        private void EnqueueNotification(INotification notification)
        {
            _ = _notificationChannel.Writer.TryWrite(notification);
        }

        /// <summary>
        ///     Drains notifications and publishes them with retries and logging.
        /// </summary>
        private async Task ProcessNotificationsAsync(CancellationToken cancellationToken)
        {
            var reader = _notificationChannel.Reader;
            while (await reader.WaitToReadAsync(cancellationToken)
                               .ConfigureAwait(false))
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
                            break; // success
                        }
                        catch (Exception ex)
                        {
                            attempt++;
                            _logger.LogWarning(
                                ex,
                                "Failed to publish notification (attempt {Attempt})",
                                attempt);
                            if (attempt < NotificationMaxRetries)
                                await Task.Delay(
                                    _notificationRetryDelay,
                                    cancellationToken)
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