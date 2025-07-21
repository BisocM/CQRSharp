using System.Diagnostics.Metrics;

namespace CQRSharp.Core.BackgroundTasks.Telemetry;

/// <summary>
///     Uses System.Diagnostics.Metrics to emit counters, gauges, and histograms,
///     and exports them via the configured Prometheus exporter.
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
    ///     Initializes a new instance of <see cref="OpenTelemetryQueueMetricsReporter" />.
    /// </summary>
    public OpenTelemetryQueueMetricsReporter()
    {
        _enqueuedCounter = Meter.CreateCounter<long>(
            "queue_items_enqueued_total",
            description: "Total number of work items ever enqueued");

        _droppedNewestCounter = Meter.CreateCounter<long>(
            "queue_items_dropped_newest_total",
            description: "Total number of work items dropped via DropNewest policy");

        _droppedOldestCounter = Meter.CreateCounter<long>(
            "queue_items_dropped_oldest_total",
            description: "Total number of work items dropped via DropOldest policy");

        _currentGauge = Meter.CreateObservableGauge(
            "queue_current_items",
            () => Interlocked.Read(ref _currentCount),
            description: "Current number of work items in the queue");

        _latencyHistogram = Meter.CreateHistogram<double>(
            "queue_item_latency_seconds",
            "s",
            "Time items spend in queue before execution");
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
    public void ItemDroppedNewest()
    {
        _droppedNewestCounter.Add(1);
    }

    /// <inheritdoc />
    public void ItemDroppedOldest()
    {
        _droppedOldestCounter.Add(1);
        Interlocked.Decrement(ref _currentCount);
    }

    /// <inheritdoc />
    public void RecordLatency(TimeSpan latency)
    {
        _latencyHistogram.Record(latency.TotalSeconds);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // No unmanaged resources to dispose
    }
}