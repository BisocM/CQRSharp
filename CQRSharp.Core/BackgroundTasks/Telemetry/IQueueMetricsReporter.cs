namespace CQRSharp.Core.BackgroundTasks.Telemetry;

/// <summary>
///     Reports queue metrics such as enqueue counts, drop counts, current length,
///     and latency histogram.
/// </summary>
public interface IQueueMetricsReporter : IDisposable
{
    /// <summary>
    ///     Gets the current number of items in the queue.
    /// </summary>
    long CurrentCount { get; }

    /// <summary>
    ///     Increment when an item is successfully enqueued.
    /// </summary>
    void ItemEnqueued();

    /// <summary>
    ///     Increment when the newest item is dropped.
    /// </summary>
    void ItemDroppedNewest();

    /// <summary>
    ///     Increment when the oldest item is dropped.
    /// </summary>
    void ItemDroppedOldest();

    /// <summary>
    ///     Records the time items spend in the queue.
    /// </summary>
    void RecordLatency(TimeSpan latency);
}