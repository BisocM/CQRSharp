using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CQRSharp.Core.BackgroundTasks.Types;
using CQRSharp.Core.Options;

namespace CQRSharp.Core.BackgroundTasks
{ 
    /// <summary>
    /// A hosted service that consumes <see cref="QueuedTask"/> instances
    /// and executes them with limited concurrency and batching.
    /// </summary>
    public class BackgroundTaskQueueConsumer : BackgroundService
    {
        private readonly ChannelReader<QueuedTask> _reader;
        private readonly ILogger<BackgroundTaskQueueConsumer> _logger;
        private readonly int _batchSize;
        private readonly SemaphoreSlim _concurrencySemaphore;

        /// <summary>
        /// Constructs a new <see cref="BackgroundTaskQueueConsumer"/>.
        /// </summary>
        public BackgroundTaskQueueConsumer(
            IBackgroundTaskQueue taskQueue,
            ILogger<BackgroundTaskQueueConsumer> logger,
            IOptions<BackgroundTaskQueueOptions> options)
        {
            _reader = taskQueue.Reader;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            var opts = options.Value ?? throw new ArgumentNullException(nameof(options));
            _batchSize = opts.DequeueBatchSize > 0
                ? opts.DequeueBatchSize
                : BackgroundTaskQueueOptions.DefaultDequeueBatchSize;

            //Single semaphore to cap total concurrency
            var maxConcurrency = opts.ConsumerCount > 0
                ? opts.ConsumerCount
                : Environment.ProcessorCount;
            _concurrencySemaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        }

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Starting consumer with max concurrency {Count} and batch size {Batch}.",
                _concurrencySemaphore.CurrentCount, _batchSize);

            while (!stoppingToken.IsCancellationRequested)
            {
                //Wait for any item
                await _reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false);

                var processed = 0;
                //Drain up to batchSize items
                while (processed < _batchSize)
                {
                    //turn the ValueTask<bool> into a Task<bool>
                    var waitToReadTask = _reader
                        .WaitToReadAsync(stoppingToken)
                        .AsTask();

                    //this is already a Task
                    var waitForSlotTask =
                        _concurrencySemaphore.WaitAsync(stoppingToken);

                    //await both in parallel
                    await Task.WhenAll(waitToReadTask, waitForSlotTask);

                    //if the channel has been completed, break
                    if (!waitToReadTask.Result)
                        break;

                    if (!_reader.TryRead(out var qt))
                    {
                        _concurrencySemaphore.Release(); //undo the slot grab
                        break;
                    }

                    _ = ProcessWorkItemAsync(qt, stoppingToken)
                        .ContinueWith(_ => _concurrencySemaphore.Release(),
                            TaskScheduler.Default);

                    processed++;
                }
            }

            _logger.LogInformation("Consumer is stopping.");
        }

        /// <summary>
        /// Executes the queued work item and logs any exception.
        /// </summary>
        private async Task ProcessWorkItemAsync(QueuedTask qt, CancellationToken cancellationToken)
        {
            try
            {
                await qt.WorkItem(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing work item {SequenceNumber}.", qt.SequenceNumber);
            }
        }
    }
}