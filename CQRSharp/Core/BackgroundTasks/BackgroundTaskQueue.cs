using System.Threading.Channels;
using CQRSharp.Core.Options;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.BackgroundTasks
{
    /// <summary>
    ///     Provides a queue for background tasks that can be processed asynchronously.
    ///     The queue can be configured to be either bounded or unbounded based on the provided options.
    /// </summary>
    public sealed class BackgroundTaskQueue : IBackgroundTaskQueue
    {
        private readonly BackgroundTaskQueueOptions _options;
        private readonly Channel<Func<CancellationToken, Task>> _workItems;

        /// <summary>
        ///     Initializes a new instance of the <see cref="BackgroundTaskQueue" /> class.
        ///     Configures the underlying channel as bounded or unbounded depending on the specified options.
        /// </summary>
        public BackgroundTaskQueue(IOptions<BackgroundTaskQueueOptions> options)
        {
            _options = options.Value;

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

        /// <inheritdoc />
        public async Task QueueBackgroundWorkItemAsync(Func<CancellationToken, Task> workItem, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(workItem);

            //Try a non‑blocking write first
            if (!_workItems.Writer.TryWrite(workItem))
            {
                //Notify rejection callback, if provided
                _options.OnTaskRejected?.Invoke(workItem);

                //Then always wait for space and enqueue
                await _workItems.Writer.WriteAsync(workItem, ct);
            }
        }

        /// <inheritdoc />
        public async Task<Func<CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken) => await _workItems.Reader.ReadAsync(cancellationToken);
    }
}