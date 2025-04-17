using System.Threading.Channels;
using CQRSharp.Core.Options;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.BackgroundTasks
{
    /// <summary>
    /// Provides a queue for background tasks that can be processed asynchronously.
    /// Supports bounded/unbounded channels with strict backpressure and minimal
    /// contention settings.
    /// </summary>
    public sealed class BackgroundTaskQueue : IBackgroundTaskQueue
    {
        private readonly BackgroundTaskQueueOptions _options;
        private readonly Channel<Func<CancellationToken, Task>> _workItems;

        /// <summary>
        /// Initializes a new instance of the <see cref="BackgroundTaskQueue"/> class.
        /// </summary>
        /// <param name="options">
        /// The options used to configure capacity, backpressure, and batch settings.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown if <paramref name="options"/> is null.
        /// </exception>
        public BackgroundTaskQueue(IOptions<BackgroundTaskQueueOptions> options)
        {
            ArgumentNullException.ThrowIfNull(options);
            _options = options.Value;

            if (_options.Capacity > 0)
            {
                var bounded = new BoundedChannelOptions(_options.Capacity)
                {
                    FullMode                      = _options.FullMode,
                    SingleReader                  = false,
                    SingleWriter                  = false,
                    AllowSynchronousContinuations = false
                };
                _workItems = Channel.CreateBounded<Func<CancellationToken, Task>>(bounded);
            }
            else
            {
                var unbounded = new UnboundedChannelOptions
                {
                    SingleReader                  = false,
                    SingleWriter                  = false,
                    AllowSynchronousContinuations = false
                };
                _workItems = Channel.CreateUnbounded<Func<CancellationToken, Task>>(unbounded);
            }
        }

        /// <inheritdoc/>
        public Task QueueBackgroundWorkItemAsync(
            Func<CancellationToken, Task> workItem,
            CancellationToken cancellationToken)
        {
            //Validate
            ArgumentNullException.ThrowIfNull(workItem);

            //If capacity is bounded and we've reached it, apply backpressure policy
            if (_options.Capacity > 0 &&
                _workItems.Reader.Count >= _options.Capacity)
            {
                //Notify that we're rejecting this item
                _options.OnTaskRejected?.Invoke(workItem);

                //Handle according to FullMode
                return _options.FullMode switch
                {
                    //Block (or cancel) until space frees up
                    BoundedChannelFullMode.Wait =>
                        _workItems.Writer.WriteAsync(workItem, cancellationToken).AsTask(),

                    //Simply drop the newest/write without enqueueing
                    BoundedChannelFullMode.DropNewest or
                        BoundedChannelFullMode.DropWrite =>
                        Task.CompletedTask,

                    //Remove oldest then enqueue this one
                    BoundedChannelFullMode.DropOldest =>
                        DropOldestAndEnqueue(workItem),

                    //If someone configures an unknown mode, that's an error
                    _ =>
                        throw new InvalidOperationException(
                            "Queue full and policy set to throw.")
                };
            }

            //Otherwise, enqueue immediately (unbounded or not yet full)
            _workItems.Writer.TryWrite(workItem);
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public ValueTask<Func<CancellationToken, Task>> DequeueAsync(
            CancellationToken cancellationToken) =>
            _workItems.Reader.ReadAsync(cancellationToken);

        /// <inheritdoc/>
        public ChannelReader<Func<CancellationToken, Task>> Reader
            => _workItems.Reader;

        private Task DropOldestAndEnqueue(Func<CancellationToken, Task> workItem)
        {
            _workItems.Reader.TryRead(out _);
            _workItems.Writer.TryWrite(workItem);
            return Task.CompletedTask;
        }
    }
}