using System.Diagnostics.Metrics;

namespace CQRSharp.Core.Background.TaskQueue.Telemetry;

/// <summary>
///     An implementation of <see cref="IQueueMetricsReporter" /> that uses the .NET
///     OpenTelemetry library to expose queue metrics.
/// </summary>
/// <remarks>
///     Each instance owns its own <see cref="Meter" /> and instruments, so disposing one reporter — e.g. when a host is
///     torn down and rebuilt in the same process, as integration tests and host-reload scenarios do — does not stop
///     metrics for any other instance. The meter name is shared, which the OpenTelemetry pipeline aggregates as usual.
/// </remarks>
public sealed class OpenTelemetryQueueMetricsReporter : IQueueMetricsReporter
{
    private const string MeterName = "CQRSharp.Core.BackgroundTasks";
    private const string MeterVersion = "1.0.0";

    private readonly ObservableGauge<long> _currentGauge;
    private readonly Counter<long> _droppedNewestCounter;
    private readonly Counter<long> _droppedOldestCounter;
    private readonly Counter<long> _enqueuedCounter;
    private readonly Histogram<double> _latencyHistogram;
    private readonly Meter _meter;

    private long _currentCount;

    /// <summary>
    ///     Initializes a new instance of the <see cref="OpenTelemetryQueueMetricsReporter" /> class.
    /// </summary>
    public OpenTelemetryQueueMetricsReporter()
    {
        _meter = new Meter(MeterName, MeterVersion);

        _enqueuedCounter = _meter.CreateCounter<long>(
            "cqrsharp.queue.items.enqueued.total",
            description: "Total number of work items ever enqueued");

        _droppedNewestCounter = _meter.CreateCounter<long>(
            "cqrsharp.queue.items.dropped.newest.total",
            description: "Total number of work items dropped because the queue was full");

        _droppedOldestCounter = _meter.CreateCounter<long>(
            "cqrsharp.queue.items.dropped.oldest.total",
            description: "Total number of work items dropped via DropOldest policy to make space");

        _currentGauge = _meter.CreateObservableGauge(
            "cqrsharp.queue.items.current",
            () => Interlocked.Read(ref _currentCount),
            description: "Current number of work items in the queue");

        _latencyHistogram = _meter.CreateHistogram<double>(
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
        _meter.Dispose();
    }
}
