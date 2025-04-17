using System.Threading.Channels;
using CQRSharp.Core.BackgroundTasks.Types;
using Microsoft.Extensions.Options;
using CQRSharp.Core.Options;

namespace CQRSharp.Core.BackgroundTasks
{
    /// <summary>
    /// Implements <see cref="IBackgroundTaskQueue"/> with optional bounded/unbounded channels,
    /// rich enqueue feedback, sequence numbers, event callbacks, and built-in metrics.
    /// </summary>
    public sealed class BackgroundTaskQueue : IBackgroundTaskQueue
    {
        private readonly BackgroundTaskQueueOptions _options;
        private readonly Channel<QueuedTask> _workItems;
        private long _sequenceGenerator, _enqueuedCount, _droppedNewestCount, _droppedOldestCount;

        /// <summary>
        /// Initializes a new instance of the <see cref="BackgroundTaskQueue"/> class.
        /// </summary>
        /// <param name="options">
        /// The options used to configure capacity, backpressure policy, and event callbacks.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="options"/> is null.</exception>
        public BackgroundTaskQueue(IOptions<BackgroundTaskQueueOptions> options)
        {
            ArgumentNullException.ThrowIfNull(options);
            _options = options.Value;

            if (_options.Capacity > 0)
            {
                var bounded = new BoundedChannelOptions(_options.Capacity)
                {
                    FullMode = _options.FullMode,
                    SingleReader = false,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                };
                _workItems = Channel.CreateBounded<QueuedTask>(bounded);
            }
            else
            {
                var unbounded = new UnboundedChannelOptions
                {
                    SingleReader = false,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                };
                _workItems = Channel.CreateUnbounded<QueuedTask>(unbounded);
            }
        }

        /// <inheritdoc/>
        public long TotalItemsEnqueued => Interlocked.Read(ref _enqueuedCount);

        /// <inheritdoc/>
        public long TotalDroppedNewest => Interlocked.Read(ref _droppedNewestCount);

        /// <inheritdoc/>
        public long TotalDroppedOldest => Interlocked.Read(ref _droppedOldestCount);

        /// <inheritdoc/>
        public event Action<TaskEnqueuedEventArgs>? OnTaskEnqueued;

        /// <inheritdoc/>
        public event Action<TaskRejectedEventArgs>? OnTaskRejected;

        /// <inheritdoc/>
        /// <remarks>
        /// Assigns a unique sequence number to each work item, applies the configured full-mode policy,
        /// updates metrics, and fires appropriate callbacks.
        /// </remarks>
        public async Task<QueueWriteResult> QueueBackgroundWorkItemAsync(
            Func<CancellationToken, Task> workItem,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(workItem);

            // Assign a unique ID for tracing
            var seq = Interlocked.Increment(ref _sequenceGenerator);
            var qt = new QueuedTask(seq, workItem);

            // If bounded and full, apply policy
            if (_options.Capacity > 0 && _workItems.Reader.Count >= _options.Capacity)
            {
                if (_options.FullMode == BoundedChannelFullMode.DropOldest &&
                    _workItems.Reader.TryRead(out var oldest))
                {
                    Interlocked.Increment(ref _droppedOldestCount);
                    var rej = new TaskRejectedEventArgs(seq, _options.FullMode, oldest.SequenceNumber);
                    _options.OnTaskRejected?.Invoke(rej);
                    OnTaskRejected?.Invoke(rej);
                }
                else if (_options.FullMode == BoundedChannelFullMode.DropNewest ||
                         _options.FullMode == BoundedChannelFullMode.DropWrite)
                {
                    Interlocked.Increment(ref _droppedNewestCount);
                    var rej = new TaskRejectedEventArgs(seq, _options.FullMode, null);
                    _options.OnTaskRejected?.Invoke(rej);
                    OnTaskRejected?.Invoke(rej);
                    return new QueueWriteResult(QueueWriteResultCode.DroppedNewest, seq);
                }

                if (_options.FullMode == BoundedChannelFullMode.Wait)
                {
                    await _workItems.Writer.WriteAsync(qt, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _enqueuedCount);
                    var enq = new TaskEnqueuedEventArgs(seq, workItem);
                    _options.OnTaskEnqueued?.Invoke(enq);
                    OnTaskEnqueued?.Invoke(enq);
                    return new QueueWriteResult(QueueWriteResultCode.Waited, seq);
                }
            }

            // Default: unbounded or not full
            if (_workItems.Writer.TryWrite(qt))
            {
                Interlocked.Increment(ref _enqueuedCount);
                var enq = new TaskEnqueuedEventArgs(seq, workItem);
                _options.OnTaskEnqueued?.Invoke(enq);
                OnTaskEnqueued?.Invoke(enq);
                return new QueueWriteResult(QueueWriteResultCode.Enqueued, seq);
            }

            // Fallback to waiting
            await _workItems.Writer.WriteAsync(qt, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _enqueuedCount);
            var fallback = new TaskEnqueuedEventArgs(seq, workItem);
            _options.OnTaskEnqueued?.Invoke(fallback);
            OnTaskEnqueued?.Invoke(fallback);
            return new QueueWriteResult(QueueWriteResultCode.Waited, seq);
        }

        /// <inheritdoc/>
        public ValueTask<QueuedTask> DequeueAsync(CancellationToken cancellationToken) =>
            _workItems.Reader.ReadAsync(cancellationToken);

        /// <inheritdoc/>
        public ChannelReader<QueuedTask> Reader => _workItems.Reader;
    }
}