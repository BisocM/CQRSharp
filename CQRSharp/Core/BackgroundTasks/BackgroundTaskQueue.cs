using System.Threading.Channels;
using CQRSharp.Core.BackgroundTasks.Types;
using CQRSharp.Core.Options;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.BackgroundTasks
{
    /// <summary>
    /// Implements <see cref="IBackgroundTaskQueue"/> with optional bounded/unbounded sharded channels,
    /// rich enqueue feedback, sequence numbers, event callbacks, and built-in metrics.
    /// Sharding reduces single‐channel contention under bursty loads.
    /// </summary>
    public sealed class BackgroundTaskQueue : IBackgroundTaskQueue
    {
        private readonly BackgroundTaskQueueOptions _options;
        private readonly Channel<QueuedTask>[] _shardChannels;
        private readonly Channel<QueuedTask> _outputChannel;
        private readonly Channel<Action> _callbackChannel;
        private readonly int _shardCount;
        private long _sequenceGenerator, _enqueuedCount, _droppedNewestCount, _droppedOldestCount;

        /// <summary>
        /// Initializes a new instance of the <see cref="BackgroundTaskQueue"/> class.
        /// </summary>
        /// <param name="options">
        /// The options used to configure capacity, backpressure policy, sharding, and event callbacks.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="options"/> is null.</exception>
        public BackgroundTaskQueue(IOptions<BackgroundTaskQueueOptions> options)
        {
            ArgumentNullException.ThrowIfNull(options);
            _options = options.Value;

            //Determine how many shards to use
            if (_options.ShardCount > 0)
                _shardCount = _options.ShardCount;
            else if (_options.Capacity > 0)
                _shardCount = Math.Min(_options.Capacity, Environment.ProcessorCount);
            else
                _shardCount = Environment.ProcessorCount;

            //Build each shard channel
            _shardChannels = new Channel<QueuedTask>[_shardCount];
            if (_options.Capacity > 0)
            {
                int baseCap = _options.Capacity / _shardCount;
                int remainder = _options.Capacity % _shardCount;
                for (int i = 0; i < _shardCount; i++)
                {
                    int cap = baseCap + (i < remainder ? 1 : 0);
                    var bounded = new BoundedChannelOptions(cap)
                    {
                        FullMode = _options.FullMode,
                        SingleReader = false,
                        SingleWriter = false,
                        AllowSynchronousContinuations = false
                    };
                    _shardChannels[i] = Channel.CreateBounded<QueuedTask>(bounded);
                }
            }
            else
            {
                for (int i = 0; i < _shardCount; i++)
                {
                    var unbounded = new UnboundedChannelOptions
                    {
                        SingleReader = false,
                        SingleWriter = false,
                        AllowSynchronousContinuations = false
                    };
                    _shardChannels[i] = Channel.CreateUnbounded<QueuedTask>(unbounded);
                }
            }

            //Create merged output channel
            _outputChannel = Channel.CreateUnbounded<QueuedTask>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

            //Create single callback dispatch channel
            _callbackChannel = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

            //Start the callback‐dispatcher thread
            Task.Factory.StartNew(
                () => ProcessCallbacksAsync().GetAwaiter().GetResult(),
                TaskCreationOptions.LongRunning);

            //Launch merging tasks for each shard
            foreach (var shard in _shardChannels)
            {
                _ = MergeShardAsync(shard.Reader, _outputChannel.Writer);
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
        /// Assigns a unique sequence number to each work item, selects a shard in round‑robin,
        /// applies the configured full‑mode policy on that shard, updates metrics, and fires callbacks.
        /// </remarks>
        public async Task<QueueWriteResult> QueueBackgroundWorkItemAsync(
            Func<CancellationToken, Task> workItem,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(workItem);

            //Assign a unique ID for tracing
            var seq = Interlocked.Increment(ref _sequenceGenerator);
            var qt  = new QueuedTask(seq, workItem);

            //Pick the shard
            int shardIndex = (int)((seq - 1) % _shardCount);
            var channel    = _shardChannels[shardIndex];
            var writer     = channel.Writer;
            var reader     = channel.Reader;

            //Try immediate enqueue
            if (writer.TryWrite(qt))
            {
                Interlocked.Increment(ref _enqueuedCount);
                EnqueueCallback(new TaskEnqueuedEventArgs(seq, workItem));
                return new QueueWriteResult(QueueWriteResultCode.Enqueued, seq);
            }

            //Shard is full: apply configured FullMode
            switch (_options.FullMode)
            {
                case BoundedChannelFullMode.DropNewest:
                case BoundedChannelFullMode.DropWrite:
                    Interlocked.Increment(ref _droppedNewestCount);
                    RejectCallback(new TaskRejectedEventArgs(
                        seq, _options.FullMode, null));
                    return new QueueWriteResult(
                        QueueWriteResultCode.DroppedNewest, seq);

                case BoundedChannelFullMode.DropOldest:
                    //Remove exactly one oldest element, if any
                    if (reader.TryRead(out var dropped))
                    {
                        var droppedSeq = dropped.SequenceNumber;
                        Interlocked.Increment(ref _droppedOldestCount);
                        
                        //Notify subscribers which sequence was dropped
                        RejectCallback(new TaskRejectedEventArgs(
                            seq, _options.FullMode, droppedSeq));
                    }

                    //Now there is space for the new item
                    await writer.WriteAsync(qt, cancellationToken)
                        .ConfigureAwait(false);
                    Interlocked.Increment(ref _enqueuedCount);
                    EnqueueCallback(new TaskEnqueuedEventArgs(seq, workItem));

                    //Return DroppedOldest to indicate we made room
                    return new QueueWriteResult(
                        QueueWriteResultCode.DroppedOldest, seq);

                case BoundedChannelFullMode.Wait:
                default:
                    await writer.WriteAsync(qt, cancellationToken)
                        .ConfigureAwait(false);
                    Interlocked.Increment(ref _enqueuedCount);
                    EnqueueCallback(new TaskEnqueuedEventArgs(seq, workItem));
                    return new QueueWriteResult(
                        QueueWriteResultCode.Waited, seq);
            }
        }

        /// <inheritdoc/>
        public ValueTask<QueuedTask> DequeueAsync(CancellationToken cancellationToken) =>
            _outputChannel.Reader.ReadAsync(cancellationToken);

        /// <inheritdoc/>
        public ChannelReader<QueuedTask> Reader => _outputChannel.Reader;

        //Merges one shard into the output channel using ReadAllAsync()
        private async Task MergeShardAsync(
            ChannelReader<QueuedTask> reader,
            ChannelWriter<QueuedTask> writer)
        {
            await foreach (var item in reader.ReadAllAsync().ConfigureAwait(false))
            {
                await writer.WriteAsync(item).ConfigureAwait(false);
            }
        }

        //Single‐threaded dispatcher for all callbacks
        private async Task ProcessCallbacksAsync()
        {
            await foreach (var invoke in _callbackChannel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try { invoke(); }
                catch { /* swallow */ }
            }
        }

        private void EnqueueCallback(TaskEnqueuedEventArgs args)
        {
            var staticHandlers = _options.OnTaskEnqueued;
            var instanceHandlers = OnTaskEnqueued;
            if (staticHandlers != null)
                _callbackChannel.Writer.TryWrite(() => staticHandlers.Invoke(args));
            if (instanceHandlers != null)
                _callbackChannel.Writer.TryWrite(() => instanceHandlers.Invoke(args));
        }

        private void RejectCallback(TaskRejectedEventArgs args)
        {
            var staticHandlers = _options.OnTaskRejected;
            var instanceHandlers = OnTaskRejected;
            if (staticHandlers != null)
                _callbackChannel.Writer.TryWrite(() => staticHandlers.Invoke(args));
            if (instanceHandlers != null)
                _callbackChannel.Writer.TryWrite(() => instanceHandlers.Invoke(args));
        }
    }
}