using System.Threading.Channels;
using CQRSharp.Core.BackgroundTasks.Types;
using CQRSharp.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.BackgroundTasks;

/// <summary>
///     Hosted‑service that drains a <see cref="BackgroundTaskQueue" /> and
///     executes work items with bounded concurrency.
/// </summary>
internal sealed class BackgroundTaskQueueConsumer : BackgroundService
{
    private readonly SemaphoreSlim _concurrency;
    private readonly ILogger<BackgroundTaskQueueConsumer> _logger;
    private readonly ChannelReader<QueuedTask> _reader;
    private readonly TimeSpan _shutdownTimeout;

    /// <inheritdoc />
    public BackgroundTaskQueueConsumer(
        IBackgroundTaskQueue taskQueue,
        ILogger<BackgroundTaskQueueConsumer> logger,
        IOptions<BackgroundTaskQueueOptions> options)
    {
        _reader = taskQueue.Reader
                  ?? throw new ArgumentNullException(nameof(taskQueue));

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var opts = options.Value
                   ?? throw new ArgumentNullException(nameof(options));

        var maxConc = opts.ConsumerCount > 0
            ? opts.ConsumerCount
            : Environment.ProcessorCount;

        _concurrency = new SemaphoreSlim(maxConc, maxConc);
        _shutdownTimeout = opts.ShutdownTimeout;
    }

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken token)
    {
        _logger.LogInformation(
            "BackgroundTaskQueueConsumer starting.  MaxConcurrency = {MC}.",
            _concurrency.CurrentCount);
        await base.StartAsync(token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false);

                while (_reader.TryRead(out var qt))
                {
                    await _concurrency.WaitAsync(stoppingToken).ConfigureAwait(false);

                    _ = ProcessWorkItemAsync(qt, stoppingToken).ContinueWith(
                        static (_, state) =>
                        {
                            var sem = (SemaphoreSlim)state!;
                            sem.Release();
                        }, _concurrency, TaskScheduler.Default);
                }
            }
        }
        catch (OperationCanceledException)
        {
            /* graceful shutdown */
        }
        finally
        {
            _logger.LogInformation("BackgroundTaskQueueConsumer execute loop ending.");
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken token)
    {
        _logger.LogInformation(
            "Consumer stopping; waiting up to {TO} for tasks to finish.",
            _shutdownTimeout);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(_shutdownTimeout);

        await base.StopAsync(cts.Token).ConfigureAwait(false);
        _logger.LogInformation("BackgroundTaskQueueConsumer stopped.");
    }

    /// <summary>Runs a single work item and handles completion / fault accounting.</summary>
    private static async Task ProcessWorkItemAsync(
        QueuedTask qt,
        CancellationToken token)
    {
        try
        {
            await qt.WorkItem(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BackgroundTaskQueue.SignalTaskException(qt.QueueId, qt.SequenceNumber, ex);
        }
        finally
        {
            BackgroundTaskQueue.CompleteTaskAsRan(qt.QueueId, qt.SequenceNumber);
        }
    }
}