using System.Threading.Channels;
using CQRSharp.Core.BackgroundTasks.Types;
using CQRSharp.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.BackgroundTasks
{
    /// <summary>
    /// Hosted service that drains a <see cref="BackgroundTaskQueue"/> and executes work items
    /// with bounded concurrency.
    /// </summary>
    internal sealed class BackgroundTaskQueueConsumer : BackgroundService
    {
        private readonly SemaphoreSlim                         _concurrency;
        private readonly ILogger<BackgroundTaskQueueConsumer> _logger;
        private readonly ChannelReader<QueuedTask>            _reader;
        private readonly TimeSpan                             _shutdownTimeout;
        private readonly BackgroundTaskQueue                  _queue;

        /// <summary>
        /// Initializes a new instance of <see cref="BackgroundTaskQueueConsumer"/>.
        /// </summary>
        public BackgroundTaskQueueConsumer(
            IBackgroundTaskQueue                   taskQueue,
            ILogger<BackgroundTaskQueueConsumer>   logger,
            IOptions<BackgroundTaskQueueOptions>   options)
        {
            _reader = taskQueue.Reader
                      ?? throw new ArgumentNullException(nameof(taskQueue));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            var opts = options.Value
                       ?? throw new ArgumentNullException(nameof(options));

            var maxConcurrency = opts.ConsumerCount > 0
                                 ? opts.ConsumerCount
                                 : Environment.ProcessorCount;

            _concurrency     = new SemaphoreSlim(maxConcurrency, maxConcurrency);
            _shutdownTimeout = opts.ShutdownTimeout;

            if (taskQueue is not BackgroundTaskQueue concrete)
            {
                throw new ArgumentException(
                    "Cannot obtain concrete BackgroundTaskQueue instance",
                    nameof(taskQueue));
            }
            _queue = concrete;
        }

        /// <inheritdoc/>
        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "BackgroundTaskQueueConsumer starting.  MaxConcurrency = {Max}.",
                _concurrency.CurrentCount);

            await base.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await _reader.WaitToReadAsync(stoppingToken)
                                 .ConfigureAwait(false);

                    while (_reader.TryRead(out var queuedTask))
                    {
                        await _concurrency.WaitAsync(stoppingToken)
                                          .ConfigureAwait(false);

                        _ = ProcessWorkItemAsync(queuedTask, stoppingToken)
                              .ContinueWith(
                                  _ => _concurrency.Release(),
                                  TaskScheduler.Default);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                //shutdown
            }
            finally
            {
                _logger.LogInformation(
                    "BackgroundTaskQueueConsumer execute loop ending.");
            }
        }

        /// <inheritdoc/>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "Consumer stopping; waiting up to {Timeout} for tasks to finish.",
                _shutdownTimeout);

            using var cts = CancellationTokenSource
                                .CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_shutdownTimeout);

            await base.StopAsync(cts.Token).ConfigureAwait(false);
            _logger.LogInformation("BackgroundTaskQueueConsumer stopped.");
        }

        private async Task ProcessWorkItemAsync(
            QueuedTask queuedTask,
            CancellationToken cancellationToken)
        {
            try
            {
                await queuedTask.WorkItem(cancellationToken)
                                .ConfigureAwait(false);
                _queue.CompleteTaskAsRan(queuedTask.SequenceNumber);
            }
            catch (Exception ex)
            {
                _queue.SignalTaskException(queuedTask.SequenceNumber, ex);
            }
        }
    }
}