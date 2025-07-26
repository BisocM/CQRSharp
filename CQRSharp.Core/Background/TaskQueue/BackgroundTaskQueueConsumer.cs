using System.Threading.Channels;
using CQRSharp.Core.Background.TaskQueue.Types;
using CQRSharp.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Background.TaskQueue;

/// <summary>
/// A background service that consumes and processes work items from the <see cref="IBackgroundTaskQueue"/>.
/// It manages concurrent task execution and ensures a graceful shutdown.
/// </summary>
internal sealed class BackgroundTaskQueueConsumer : BackgroundService
{
    private readonly ILogger<BackgroundTaskQueueConsumer> _logger;
    private readonly ChannelReader<QueuedTask> _reader;
    private readonly SemaphoreSlim _concurrencyLimiter;
    private readonly TimeSpan _shutdownTimeout;

    /// <summary>
    /// A collection of tasks that are currently being processed. Used to ensure graceful shutdown.
    /// Access to this list must be synchronized.
    /// </summary>
    private readonly List<Task> _processingTasks = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="BackgroundTaskQueueConsumer"/> class.
    /// </summary>
    /// <param name="taskQueue">The background task queue to consume from.</param>
    /// <param name="logger">The logger for this consumer.</param>
    /// <param name="options">The configuration options for the queue.</param>
    public BackgroundTaskQueueConsumer(
        IBackgroundTaskQueue taskQueue,
        ILogger<BackgroundTaskQueueConsumer> logger,
        IOptions<BackgroundTaskQueueOptions> options)
    {
        _reader = taskQueue.Reader ?? throw new ArgumentNullException(nameof(taskQueue.Reader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var opts = options.Value ?? throw new ArgumentNullException(nameof(options));

        // Determine the maximum number of concurrent tasks, defaulting to the processor count.
        var maxConcurrency = opts.ConsumerCount > 0 ? opts.ConsumerCount : Environment.ProcessorCount;
        
        _concurrencyLimiter = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _shutdownTimeout = opts.ShutdownTimeout;
    }

    /// <summary>
    /// The main execution method for the background service. It reads from the queue and dispatches tasks
    /// for processing until a shutdown is requested.
    /// </summary>
    /// <param name="stoppingToken">A token that is cancelled when the application is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Background task queue consumer is starting with max concurrency of {MaxConcurrency}.", _concurrencyLimiter.CurrentCount);

        try
        {
            // Continuously wait for items to become available in the channel.
            while (await _reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                // Process all items currently in the channel.
                while (_reader.TryRead(out var queuedTask))
                {
                    // Wait for a concurrency slot to become available.
                    await _concurrencyLimiter.WaitAsync(stoppingToken).ConfigureAwait(false);

                    // Create a task to process the work item.
                    var processingTask = ProcessWorkItemAsync(queuedTask, stoppingToken);
                    
                    // Track the running task for graceful shutdown.
                    TrackTask(processingTask);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Background task queue consumer is shutting down.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "A fatal error occurred in the background task consumer loop.");
        }
        finally
        {
            // This block ensures that upon shutdown, we wait for all active tasks to finish.
            _logger.LogInformation("Consumer loop ending. Waiting for {Count} active task(s) to complete.", _processingTasks.Count);
            
            // Create a snapshot of the tasks to wait for.
            Task[] tasksToWaitFor;
            lock (_processingTasks)
            {
                tasksToWaitFor = _processingTasks.ToArray();
            }

            try
            {
                // Wait for all tasks to complete, with a final shutdown timeout.
                using var cts = new CancellationTokenSource(_shutdownTimeout);
                var allTasks = Task.WhenAll(tasksToWaitFor);
                await allTasks.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Graceful shutdown timed out after {Timeout}. Some background tasks may not have completed.", _shutdownTimeout);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while waiting for active tasks to complete during shutdown.");
            }
            
            _logger.LogInformation("All active tasks have completed. Consumer stopped.");
        }
    }

    /// <summary>
    /// Tracks a running task and sets up a continuation to untrack it upon completion.
    /// </summary>
    /// <param name="task">The task to track.</param>
    private void TrackTask(Task task)
    {
        lock (_processingTasks)
        {
            _processingTasks.Add(task);
        }

        // When the task completes (successfully, faulted, or canceled), remove it from the tracking list.
        _ = task.ContinueWith(t =>
        {
            lock (_processingTasks)
            {
                _processingTasks.Remove(t);
            }
            _concurrencyLimiter.Release();
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Executes a single work item and logs any exceptions that occur.
    /// </summary>
    /// <param name="queuedTask">The queued task to process.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task ProcessWorkItemAsync(QueuedTask queuedTask, CancellationToken cancellationToken)
    {
        try
        {
            // Execute the user's work item.
            await queuedTask.WorkItem(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Catch and log any unhandled exceptions from the work item to prevent the consumer from crashing.
            _logger.LogError(ex, "A background work item threw an unhandled exception during execution.");
        }
    }

    /// <summary>
    /// Overrides the default StopAsync to provide custom shutdown logging.
    /// The primary shutdown logic is now handled in the finally block of <see cref="ExecuteAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">A token to signal that the shutdown process should no longer be graceful.</param>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Consumer stopping. Graceful shutdown initiated.");
        // The base method triggers cancellation on the token passed to ExecuteAsync.
        return base.StopAsync(cancellationToken);
    }
}