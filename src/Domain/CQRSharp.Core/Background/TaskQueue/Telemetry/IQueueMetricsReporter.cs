namespace CQRSharp.Core.Background.TaskQueue.Telemetry;

/// <summary>
///     Defines an interface for reporting metrics related to the background task queue.
///     This allows for pluggable metrics implementations, such as OpenTelemetry or custom loggers.
/// </summary>
public interface IQueueMetricsReporter : IDisposable
{
    /// <summary>
    ///     Gets the current number of items in the queue.
    /// </summary>
    long CurrentCount { get; }

    /// <summary>
    ///     Reports that a new work item has been successfully enqueued.
    ///     This should increment the total enqueued count and the current queue size.
    /// </summary>
    void ItemEnqueued();

    /// <summary>
    ///     Reports that a work item has been successfully dequeued for processing.
    ///     This should decrement the current queue size.
    /// </summary>
    void ItemDequeued();

    /// <summary>
    ///     Reports that the newest work item was dropped because the queue was full,
    ///     consistent with the <see cref="System.Threading.Channels.BoundedChannelFullMode.DropWrite" /> policy.
    /// </summary>
    void ItemDroppedNewest();

    /// <summary>
    ///     Reports that the oldest work item was dropped from the queue to make space for a new one,
    ///     consistent with the <see cref="System.Threading.Channels.BoundedChannelFullMode.DropOldest" /> policy.
    ///     This should decrement the current queue size.
    /// </summary>
    void ItemDroppedOldest();

    /// <summary>
    ///     Records the total time a work item spent in the queue before being processed.
    /// </summary>
    /// <param name="latency">The duration the item was in the queue.</param>
    void RecordLatency(TimeSpan latency);
}