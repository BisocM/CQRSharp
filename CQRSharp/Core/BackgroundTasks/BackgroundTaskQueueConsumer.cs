using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CQRSharp.Core.Options;

namespace CQRSharp.Core.BackgroundTasks
{
    /// <summary>
    /// A <see cref="BackgroundService"/> that continuously consumes work items
    /// from an <see cref="IBackgroundTaskQueue"/> and dispatches them to the thread pool.
    /// </summary>
    public class BackgroundTaskQueueConsumer : BackgroundService
    {
        private readonly IBackgroundTaskQueue _taskQueue;
        private readonly ILogger<BackgroundTaskQueueConsumer> _logger;
        private readonly int _batchSize;

        /// <summary>
        /// Initializes a new instance of the <see cref="BackgroundTaskQueueConsumer"/> class.
        /// </summary>
        /// <param name="taskQueue">The queue to consume from.</param>
        /// <param name="logger">Logger for lifecycle and error events.</param>
        /// <param name="options">Queue options for batch sizing.</param>
        /// <exception cref="ArgumentNullException">Thrown if any dependency is null.</exception>
        public BackgroundTaskQueueConsumer(
            IBackgroundTaskQueue taskQueue,
            ILogger<BackgroundTaskQueueConsumer> logger,
            IOptions<BackgroundTaskQueueOptions> options)
        {
            _taskQueue = taskQueue ?? throw new ArgumentNullException(nameof(taskQueue));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            var opts = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _batchSize = opts.DequeueBatchSize > 0 ? opts.DequeueBatchSize : int.MaxValue;
        }

        /// <summary>
        /// Executes the background consumer loop, reading tasks in batches and queuing them
        /// on the thread pool for execution.
        /// </summary>
        /// <param name="stoppingToken">A token that signals when the host is shutting down.</param>
        /// <returns>A <see cref="Task"/> that completes when the consumer stops.</returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("BackgroundTaskQueueConsumer starting.");

            var reader = _taskQueue.Reader;
            while (await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                var processed = 0;
                while (processed++ < _batchSize && reader.TryRead(out var qt))
                {
                    ThreadPool.UnsafeQueueUserWorkItem(async void (_) =>
                    {
                        try
                        {
                            await qt.WorkItem(stoppingToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error executing work item {SequenceNumber}.", qt.SequenceNumber);
                        }
                    }, null);
                }
            }

            _logger.LogInformation("BackgroundTaskQueueConsumer stopping.");
        }
    }
}