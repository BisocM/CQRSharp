using System.Diagnostics.Metrics;

namespace CQRSharp.Core.Background.TaskQueue.Telemetry;

/// <summary>
///     An implementation of <see cref="IQueueMetricsReporter" /> that uses the .NET
///     OpenTelemetry library to expose queue metrics.
/// </summary>
public sealed class OpenTelemetryQueueMetricsReporter : IQueueMetricsReporter
{
    private const string MeterName = "CQRSharp.Core.BackgroundTasks";
    private const string MeterVersion = "1.0.0";

    private static readonly Meter Meter = new(MeterName, MeterVersion);
    private readonly ObservableGauge<long> _currentGauge;
    private readonly Counter<long> _droppedNewestCounter;
    private readonly Counter<long> _droppedOldestCounter;

    private readonly Counter<long> _enqueuedCounter;
    private readonly Histogram<double> _latencyHistogram;

    private long _currentCount;

    /// <summary>
    ///     Initializes a new instance of the <see cref="OpenTelemetryQueueMetricsReporter" /> class.
    /// </summary>
    public OpenTelemetryQueueMetricsReporter()
    {
        _enqueuedCounter = Meter.CreateCounter<long>(
            "cqrsharp.queue.items.enqueued.total",
            description: "Total number of work items ever enqueued");

        _droppedNewestCounter = Meter.CreateCounter<long>(
            "cqrsharp.queue.items.dropped.newest.total",
            description: "Total number of work items dropped because the queue was full");

        _droppedOldestCounter = Meter.CreateCounter<long>(
            "cqrsharp.queue.items.dropped.oldest.total",
            description: "Total number of work items dropped via DropOldest policy to make space");

        _currentGauge = Meter.CreateObservableGauge(
            "cqrsharp.queue.items.current",
            () => Interlocked.Read(ref _currentCount),
            description: "Current number of work items in the queue");

        _latencyHistogram = Meter.CreateHistogram<double>(
            "cqrsharp.queue.item.latency.seconds",
            "s",
            "Time items spend in queue before being processed");
    }

    /// <inheritdoc />
    public long CurrentCount => Interlocked.Read(ref _currentCount);

    /// <inheritdoc />
    public void ItemEnqueued()
    {
        _enqueuedCounter.Add(1);
        Interlocked.Increment(ref _currentCount);
    }

    /// <inheritdoc />
    public void ItemDequeued()
    {
        Interlocked.Decrement(ref _currentCount);
    }

    /// <inheritdoc />
    public void ItemDroppedNewest()
    {
        _droppedNewestCounter.Add(1);
    }

    /// <inheritdoc />
    public void ItemDroppedOldest()
    {
        _droppedOldestCounter.Add(1);
        Interlocked.Decrement(ref _currentCount); // An old item is removed, so the count decreases.
    }

    /// <inheritdoc />
    public void RecordLatency(TimeSpan latency)
    {
        _latencyHistogram.Record(latency.TotalSeconds);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Meter.Dispose();
    }
}