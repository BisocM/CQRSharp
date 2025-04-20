using System.Threading.Channels;
using CQRSharp.Core.BackgroundTasks.Types;
using CQRSharp.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.BackgroundTasks
{
    /// <summary>
    /// Hosted service that continuously consumes <see cref="QueuedTask"/> instances
    /// from a <see cref="BackgroundTaskQueue"/> and executes them with bounded concurrency.
    /// Honors a graceful shutdown timeout.
    /// </summary>
    public sealed class BackgroundTaskQueueConsumer : BackgroundService
    {
        private readonly ChannelReader<QueuedTask> _reader;
        private readonly SemaphoreSlim            _concurrencySemaphore;
        private readonly ILogger<BackgroundTaskQueueConsumer> _logger;
        private readonly TimeSpan _shutdownTimeout;

        /// <summary>
        /// Constructs a new consumer.
        /// </summary>
        /// <param name="taskQueue">The queue to consume tasks from.</param>
        /// <param name="logger">Logger for status and errors.</param>
        /// <param name="options">Configuration for concurrency and shutdown timeout.</param>
        public BackgroundTaskQueueConsumer(
            IBackgroundTaskQueue taskQueue,
            ILogger<BackgroundTaskQueueConsumer> logger,
            IOptions<BackgroundTaskQueueOptions> options)
        {
            _reader = taskQueue.Reader 
                ?? throw new ArgumentNullException(nameof(taskQueue));
            _logger = logger 
                ?? throw new ArgumentNullException(nameof(logger));

            var opts = options.Value 
                ?? throw new ArgumentNullException(nameof(options));

            int maxConcurrency = opts.ConsumerCount > 0
                ? opts.ConsumerCount
                : Environment.ProcessorCount;

            _concurrencySemaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
            _shutdownTimeout      = opts.ShutdownTimeout;
        }

        /// <inheritdoc/>
        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "BackgroundTaskQueueConsumer starting with max concurrency {Count}.",
                _concurrencySemaphore.CurrentCount);
            await base.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await _reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false);

                    while (_reader.TryRead(out var queuedTask))
                    {
                        await _concurrencySemaphore.WaitAsync(stoppingToken).ConfigureAwait(false);

                        _ = ProcessWorkItemAsync(queuedTask, stoppingToken)
                            .ContinueWith(_ => _concurrencySemaphore.Release(),
                                          TaskScheduler.Default);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                //Expected on shutdown
            }
            finally
            {
                _logger.LogInformation("BackgroundTaskQueueConsumer execute loop ending.");
            }
        }

        /// <inheritdoc/>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "Consumer stopping: waiting up to {Timeout} for in‑flight tasks to complete.",
                _shutdownTimeout);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_shutdownTimeout);

            await base.StopAsync(cts.Token).ConfigureAwait(false);

            _logger.LogInformation("BackgroundTaskQueueConsumer stopped.");
        }

        /// <summary>
        /// Executes a single queued work item, logs any exception, and signals completion.
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
                //Signal exceptions to any registered observers
                BackgroundTaskQueue.SignalTaskException(qt.SequenceNumber, ex);

                _logger.LogError(
                    ex,
                    "Error executing work item {SequenceNumber}.",
                    qt.SequenceNumber);
            }
            finally
            {
                //Free up any tracking resources
                BackgroundTaskQueue.CompleteTaskAsRan(qt.SequenceNumber);
            }
        }
    }
}