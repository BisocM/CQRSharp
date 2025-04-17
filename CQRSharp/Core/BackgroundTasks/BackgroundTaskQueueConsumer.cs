using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.BackgroundTasks
{
    /// <summary>
    /// Continuously pulls work items from the BackgroundTaskQueue and executes them.
    /// </summary>
    public class BackgroundTaskQueueConsumer : BackgroundService
    {
        private readonly IBackgroundTaskQueue _taskQueue;
        private readonly ILogger<BackgroundTaskQueueConsumer> _logger;

        /// <inheritdoc />
        public BackgroundTaskQueueConsumer(
            IBackgroundTaskQueue taskQueue,
            ILogger<BackgroundTaskQueueConsumer> logger)
        {
            _taskQueue = taskQueue;
            _logger    = logger;
        }

        /// <inheritdoc />
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("BackgroundTaskQueueConsumer starting.");

            while (!stoppingToken.IsCancellationRequested)
            {
                Func<CancellationToken, Task> workItem;
                try
                {
                    workItem = await _taskQueue.DequeueAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    await workItem(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error executing background work item.");
                }
            }

            _logger.LogInformation("BackgroundTaskQueueConsumer stopping.");
        }
    }
}