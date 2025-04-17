using CQRSharp.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.BackgroundTasks
{
    /// <summary>
    /// Hosted service that continuously pulls work items in batches
    /// and dispatches them via the ThreadPool with minimal overhead.
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
        /// <exception cref="ArgumentNullException">
        /// Thrown if any dependency is null.
        /// </exception>
        public BackgroundTaskQueueConsumer(
            IBackgroundTaskQueue taskQueue,
            ILogger<BackgroundTaskQueueConsumer> logger,
            IOptions<BackgroundTaskQueueOptions> options)
        {
            _taskQueue = taskQueue    ?? throw new ArgumentNullException(nameof(taskQueue));
            _logger    = logger       ?? throw new ArgumentNullException(nameof(logger));
            ArgumentNullException.ThrowIfNull(options);

            var opts = options.Value;
            _batchSize = opts.DequeueBatchSize > 0
                ? opts.DequeueBatchSize
                : int.MaxValue;
        }

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("BackgroundTaskQueueConsumer starting.");

            var reader = _taskQueue.Reader;
            while (await reader.WaitToReadAsync(stoppingToken))
            {
                var processed = 0;
                while (processed++ < _batchSize && reader.TryRead(out var workItem))
                {
                    ThreadPool.UnsafeQueueUserWorkItem(state =>
                    {
                        workItem(stoppingToken)
                          .ContinueWith(t =>
                          {
                              if (t.Exception is not null)
                                  _logger.LogError(
                                      t.Exception,
                                      "Error executing background work item.");
                          }, TaskContinuationOptions.OnlyOnFaulted);
                    }, null);
                }
            }

            _logger.LogInformation("BackgroundTaskQueueConsumer stopping.");
        }
    }
}