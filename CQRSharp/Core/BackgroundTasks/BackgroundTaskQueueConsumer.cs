using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CQRSharp.Core.Options;
using CQRSharp.Core.BackgroundTasks.Types;

namespace CQRSharp.Core.BackgroundTasks
{
    /// <summary>
    ///     A hosted service that consumes <see cref="QueuedTask"/> instances
    ///     and executes them with limited concurrency.
    /// </summary>
    public class BackgroundTaskQueueConsumer : BackgroundService
    {
        private readonly ChannelReader<QueuedTask> _reader;
        private readonly ILogger<BackgroundTaskQueueConsumer> _logger;
        private readonly SemaphoreSlim _concurrencySemaphore;

        /// <summary>
        ///     Constructs a new <see cref="BackgroundTaskQueueConsumer"/>.
        /// </summary>
        /// <param name="taskQueue">
        ///     The background task queue to consume tasks from.
        /// </param>
        /// <param name="logger">
        ///     Logger for reporting status and errors.
        /// </param>
        /// <param name="options">
        ///     Configuration options for queue consumption.
        /// </param>
        public BackgroundTaskQueueConsumer(
            IBackgroundTaskQueue taskQueue,
            ILogger<BackgroundTaskQueueConsumer> logger,
            IOptions<BackgroundTaskQueueOptions> options)
        {
            _reader = taskQueue.Reader;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            var opts = options.Value ?? throw new ArgumentNullException(nameof(options));

            //Single semaphore to cap total concurrency
            var maxConcurrency = opts.ConsumerCount > 0
                ? opts.ConsumerCount
                : Environment.ProcessorCount;
            _concurrencySemaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        }

        /// <inheritdoc />
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "Starting consumer with max concurrency {Count}.",
                _concurrencySemaphore.CurrentCount);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    //Wait until at least one item is available
                    await _reader
                        .WaitToReadAsync(stoppingToken)
                        .ConfigureAwait(false);

                    //Drain all available tasks
                    while (_reader.TryRead(out var queuedTask))
                    {
                        //Acquire a concurrency slot
                        await _concurrencySemaphore
                            .WaitAsync(stoppingToken)
                            .ConfigureAwait(false);

                        //Execute the work item and release the slot when done
                        _ = ProcessWorkItemAsync(queuedTask, stoppingToken)
                            .ContinueWith(
                                _ => _concurrencySemaphore.Release(),
                                TaskScheduler.Default);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                //Expected on shutdown; Swallow
            }
            finally
            {
                _logger.LogInformation("Consumer is stopping.");
            }
        }

        /// <summary>
        ///     Executes the queued work item and logs any exception.
        /// </summary>
        /// <param name="qt">The queued task to process.</param>
        /// <param name="cancellationToken">Token to observe for cancellation.</param>
        private async Task ProcessWorkItemAsync(
            QueuedTask qt,
            CancellationToken cancellationToken)
        {
            try
            {
                await qt.WorkItem(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error executing work item {SequenceNumber}.",
                    qt.SequenceNumber);
            }
        }
    }
}