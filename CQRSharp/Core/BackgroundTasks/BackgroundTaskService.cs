using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.BackgroundTasks
{
    public class BackgroundTaskService(IBackgroundTaskQueue taskQueue, ILogger<BackgroundTaskService> logger)
        : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("Background Task Service is starting.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var workItem = await taskQueue.DequeueAsync(stoppingToken);

                    //Start the work item without awaiting it
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await workItem(stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Error occurred executing background work item.");
                        }
                    }, stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error occurred dequeuing work item.");
                }
            }

            logger.LogInformation("Background Task Service is stopping.");
        }
    }
}