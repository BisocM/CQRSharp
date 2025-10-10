using System.Threading.Channels;
using CQRSharp.Core.Background.TaskQueue.Telemetry;

namespace CQRSharp.Core.Background.TaskQueue.Types;

/// <summary>
///     A decorator for <see cref="ChannelReader{T}" /> that updates queue metrics
///     whenever a work item is successfully read.
/// </summary>
internal sealed class CountingChannelReader : ChannelReader<QueuedTask>
{
    private readonly ChannelReader<QueuedTask> _inner;
    private readonly IQueueMetricsReporter _metrics;

    /// <summary>
    ///     Initializes a new instance of the <see cref="CountingChannelReader" /> class.
    /// </summary>
    /// <param name="inner">The underlying channel reader to decorate.</param>
    /// <param name="metrics">The metrics reporter to update.</param>
    public CountingChannelReader(ChannelReader<QueuedTask> inner, IQueueMetricsReporter metrics)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    /// <summary>
    ///     Updates the metrics after a task has been successfully read from the channel.
    /// </summary>
    /// <param name="task">The task that was read.</param>
    private void OnItemRead(QueuedTask task)
    {
        _metrics.ItemDequeued();
        _metrics.RecordLatency(DateTime.UtcNow - task.EnqueueTime);
    }

    /// <inheritdoc />
    public override bool TryRead(out QueuedTask item)
    {
        var ok = _inner.TryRead(out item);
        if (ok) OnItemRead(item);
        return ok;
    }

    /// <inheritdoc />
    public override async ValueTask<QueuedTask> ReadAsync(CancellationToken cancellationToken = default)
    {
        var task = await _inner.ReadAsync(cancellationToken).ConfigureAwait(false);
        OnItemRead(task);
        return task;
    }

    /// <inheritdoc />
    public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
        _inner.WaitToReadAsync(cancellationToken);
}