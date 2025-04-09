using Microsoft.Extensions.Options;
using System.Threading.Channels;
using CQRSharp.Core.Options;

namespace CQRSharp.Core.BackgroundTasks
{
    /// <summary>
    /// Provides a queue for background tasks that can be processed asynchronously.
    /// The queue can be configured to be either bounded or unbounded based on the provided options.
    /// </summary>
    public sealed class BackgroundTaskQueue : IBackgroundTaskQueue
    {
        private readonly Channel<Func<CancellationToken, Task>> _workItems;
        private readonly BackgroundTaskQueueOptions _options;

        /// <summary>
        /// Initializes a new instance of the <see cref="BackgroundTaskQueue"/> class.
        /// Configures the underlying channel as bounded or unbounded depending on the specified options.
        /// </summary>
        /// <param name="options">
        /// The options used to configure the background task queue. These options control the capacity and
        /// behavior when the queue is full, as well as providing a callback for rejected tasks.
        /// </param>
        public BackgroundTaskQueue(IOptions<BackgroundTaskQueueOptions> options)
        {
            _options = options.Value;
            
            //Decide whether to use a bounded or unbounded channel.
            if (_options.Capacity > 0)
            {
                var channelOptions = new BoundedChannelOptions(_options.Capacity)
                {
                    FullMode = _options.FullMode
                };

                _workItems = Channel.CreateBounded<Func<CancellationToken, Task>>(channelOptions);
            }
            else
                _workItems = Channel.CreateUnbounded<Func<CancellationToken, Task>>();
        }

        /// <summary>
        /// Queues a background work item for asynchronous execution.
        /// </summary>
        /// <param name="workItem">
        /// A delegate representing the work item to be executed. The delegate accepts a <see cref="CancellationToken"/>
        /// and returns a <see cref="Task"/> that represents the asynchronous operation.
        /// </param>
        /// <param name="ct">
        /// A cancellation token that can be used to cancel the enqueue operation.
        /// </param>
        /// <returns>
        /// A task that completes when the work item has been successfully queued.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when the <paramref name="workItem"/> parameter is <c>null</c>.
        /// </exception>
        public async Task QueueBackgroundWorkItemAsync(Func<CancellationToken, Task> workItem, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(workItem);
            
            //Attempt to write the work item to the queue without blocking.
            //If the write fails and an OnTaskRejected callback is provided, invoke the callback.
            //Otherwise, wait until there is space in the queue.
            if (!_workItems.Writer.TryWrite(workItem))
            {
                if (_options.OnTaskRejected is not null)
                    _options.OnTaskRejected.Invoke(workItem);
                else
                    //Default behavior: Wait until a slot becomes available to enqueue the work item.
                    await _workItems.Writer.WriteAsync(workItem, ct);
            }
        }

        /// <summary>
        /// Dequeues a background work item asynchronously.
        /// </summary>
        /// <param name="cancellationToken">
        /// A cancellation token that can be used to cancel the dequeue operation.
        /// </param>
        /// <returns>
        /// A <see cref="Task"/> representing the asynchronous operation. The task result is a delegate 
        /// that accepts a <see cref="CancellationToken"/> and returns a <see cref="Task"/> representing the work item.
        /// </returns>
        public async Task<Func<CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken) => await _workItems.Reader.ReadAsync(cancellationToken);
    }
}