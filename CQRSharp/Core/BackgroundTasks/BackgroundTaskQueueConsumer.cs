using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CQRSharp.Core.Options;
using CQRSharp.Core.BackgroundTasks.Types;

namespace CQRSharp.Core.BackgroundTasks
{
    /// <summary>
    /// A hosted background service that continuously consumes <see cref="QueuedTask"/> instances
    /// from an <see cref="IBackgroundTaskQueue"/> and dispatches them for execution.
    /// </summary>
    public class BackgroundTaskQueueConsumer : BackgroundService
    {
        private readonly IBackgroundTaskQueue _taskQueue;
        private readonly ILogger<BackgroundTaskQueueConsumer> _logger;
        private readonly int _batchSize;
        private readonly int _consumerCount;

        /// <summary>
        /// Creates a new <see cref="BackgroundTaskQueueConsumer"/>.
        /// </summary>
        /// <param name="taskQueue">The background task queue to consume from.</param>
        /// <param name="logger">Logger for lifecycle and error events.</param>
        /// <param name="options">Configuration options for queue consumption.</param>
        /// <exception cref="ArgumentNullException">Thrown if any argument is null.</exception>
        public BackgroundTaskQueueConsumer(
            IBackgroundTaskQueue taskQueue,
            ILogger<BackgroundTaskQueueConsumer> logger,
            IOptions<BackgroundTaskQueueOptions> options)
        {
            _taskQueue = taskQueue ?? throw new ArgumentNullException(nameof(taskQueue));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            var opts = options.Value ?? throw new ArgumentNullException(nameof(options));
            _batchSize     = opts.DequeueBatchSize > 0 ? opts.DequeueBatchSize : int.MaxValue;
            _consumerCount = opts.ConsumerCount     > 0 ? opts.ConsumerCount     : Environment.ProcessorCount;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Spins up <c>ConsumerCount</c> parallel loops that each drain up to <c>DequeueBatchSize</c>
        /// items per wake‑up, dispatching each onto the thread‑pool.
        /// </remarks>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "BackgroundTaskQueueConsumer starting with {Count} consumers.",
                _consumerCount);

            //Launch multiple consumer loops in parallel
            var consumers = new List<Task>(_consumerCount);
            for (int i = 0; i < _consumerCount; i++)
            {
                consumers.Add(ConsumeLoopAsync(stoppingToken));
            }

            //Wait until all loops observe cancellation
            await Task.WhenAll(consumers).ConfigureAwait(false);

            _logger.LogInformation("BackgroundTaskQueueConsumer stopping.");
        }

        /// <summary>
        /// Core loop for each consumer instance: waits for available work, then reads
        /// and dispatches up to <c>_batchSize</c> tasks in one batch.
        /// </summary>
        /// <param name="stoppingToken">Token signaled when the host is shutting down.</param>
        private async Task ConsumeLoopAsync(CancellationToken stoppingToken)
        {
            var reader = _taskQueue.Reader;

            while (await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                int processed = 0;
                while (processed++ < _batchSize && reader.TryRead(out var qt))
                {
                    //Fire-and-forget dispatch; exceptions are caught/logged inside.
                    _ = ProcessWorkItemAsync(qt, stoppingToken);
                }
            }
        }

        /// <summary>
        /// Executes the queued work item and logs any exception.
        /// </summary>
        /// <param name="qt">The queued task encapsulating work and sequence number.</param>
        /// <param name="cancellationToken">Token for task cancellation.</param>
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
        }
    }
}