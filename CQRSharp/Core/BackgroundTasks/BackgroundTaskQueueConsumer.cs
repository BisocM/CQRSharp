using System.Threading.Channels;
using CQRSharp.Core.BackgroundTasks.Types;
using CQRSharp.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.BackgroundTasks
{
    /// <summary>
    /// A hosted background service that continuously consumes <see cref="QueuedTask"/> instances
    /// from a <see cref="BackgroundTaskQueue"/> and executes them with a bounded degree of concurrency.
    /// </summary>
    public sealed class BackgroundTaskQueueConsumer : BackgroundService
    {
        private readonly ChannelReader<QueuedTask> _reader;
        private readonly SemaphoreSlim            _concurrencySemaphore;
        private readonly ILogger<BackgroundTaskQueueConsumer> _logger;

        /// <summary>
        /// Constructs a new consumer.
        /// </summary>
        /// <param name="taskQueue">The queue to consume tasks from.</param>
        /// <param name="logger">Logger for status and errors.</param>
        /// <param name="options">Configuration for maximum concurrency.</param>
        public BackgroundTaskQueueConsumer(
            IBackgroundTaskQueue taskQueue,
            ILogger<BackgroundTaskQueueConsumer> logger,
            IOptions<BackgroundTaskQueueOptions> options)
        {
            _reader = taskQueue?.Reader 
                ?? throw new ArgumentNullException(nameof(taskQueue));
            _logger = logger 
                ?? throw new ArgumentNullException(nameof(logger));

            var opts = options?.Value 
                ?? throw new ArgumentNullException(nameof(options));

            int maxConcurrency = opts.ConsumerCount > 0
                ? opts.ConsumerCount
                : Environment.ProcessorCount;

            _concurrencySemaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        }

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "BackgroundTaskQueueConsumer starting with max concurrency {Count}.",
                _concurrencySemaphore.CurrentCount);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    //Wait until a work item is available
                    await _reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false);

                    //Drain all currently available tasks
                    while (_reader.TryRead(out var queuedTask))
                    {
                        //Acquire a concurrency slot
                        await _concurrencySemaphore.WaitAsync(stoppingToken).ConfigureAwait(false);

                        //Execute the work item without blocking this loop
                        _ = ProcessWorkItemAsync(queuedTask, stoppingToken)
                            .ContinueWith(_ => _concurrencySemaphore.Release(),
                                          TaskScheduler.Default);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                //Expected when the host is shutting down.
            }
            finally
            {
                _logger.LogInformation("BackgroundTaskQueueConsumer is stopping.");
            }
        }

        /// <summary>
        /// Executes a single queued work item, logging any exception, and signals completion.
        /// </summary>
        /// <param name="qt">The queued task to process.</param>
        /// <param name="cancellationToken">Token to observe for cancellation.</param>
        private async Task ProcessWorkItemAsync(QueuedTask qt, CancellationToken cancellationToken)
        {
            try
            {
                await qt.WorkItem(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error executing work item {SequenceNumber}.",
                    qt.SequenceNumber);
            }
            finally
            {
                //Remove from the dropped‑task tracker so resources are freed
                BackgroundTaskQueue.CompleteTaskAsRan(qt.SequenceNumber);
            }
        }
    }
}