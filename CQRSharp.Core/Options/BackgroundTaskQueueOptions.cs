using System.Threading.Channels;
using CQRSharp.Core.BackgroundTasks;

namespace CQRSharp.Core.Options;

/// <summary>
///     Configuration options for the <see cref="BackgroundTaskQueue" />.
/// </summary>
public sealed class BackgroundTaskQueueOptions
{
    /// <summary>
    ///     Default capacity for the internal callback notification channel.
    /// </summary>
    internal const int DefaultCallbackChannelCapacity = 1024;

    /// <summary>
    ///     Maximum number of work items the queue may hold.
    ///     Must be greater than zero; if configured otherwise, queue construction will fail.
    /// </summary>
    public int Capacity { get; set; } = 1000;

    /// <summary>
    ///     Policy to apply when <see cref="Capacity" /> is reached.
    ///     <list type="bullet">
    ///         <item>
    ///             <see cref="BoundedChannelFullMode.DropNewest" /> (default):
    ///             immediately rejects the incoming work item.
    ///         </item>
    ///         <item>
    ///             <see cref="BoundedChannelFullMode.DropOldest" />:
    ///             removes the oldest queued item before enqueuing the new one.
    ///         </item>
    ///         <item>
    ///             <see cref="BoundedChannelFullMode.Wait" />:
    ///             asynchronously waits until space becomes available.
    ///         </item>
    ///     </list>
    /// </summary>
    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.DropNewest;

    /// <summary>
    ///     Number of parallel consumer loops to start.
    ///     Defaults to <c>Environment.ProcessorCount</c>.
    ///     If set to zero or negative, the default will be used instead.
    /// </summary>
    public int ConsumerCount { get; set; } = Environment.ProcessorCount;

    /// <summary>
    ///     Capacity of the callback channel used to publish <c>TaskEnqueuedNotification</c>
    ///     and <c>TaskRejectedNotification</c> events.
    ///     When full, the oldest notifications are dropped to avoid deadlock.
    /// </summary>
    public int CallbackChannelCapacity { get; set; } = DefaultCallbackChannelCapacity;

    /// <summary>
    ///     When <c>true</c>, enables emission of built‑in metrics (e.g. total enqueued,
    ///     total dropped, current queue length) via the configured metrics reporter.
    ///     Default is <c>false</c>.
    /// </summary>
    public bool EnableMetrics { get; set; } = false;

    /// <summary>
    ///     Maximum number of retry attempts for transient failures encountered
    ///     while dispatching notifications.
    ///     The dispatcher will retry up to this many times before giving up.
    /// </summary>
    public int NotificationMaxRetries { get; set; } = 3;

    /// <summary>
    ///     Delay between consecutive notification retry attempts.
    ///     A value of <c>TimeSpan.Zero</c> causes immediate retries.
    /// </summary>
    public TimeSpan NotificationRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     When the host begins shutting down, how long to wait for in‑flight tasks
    ///     to complete gracefully before cancelling them.
    ///     Default is 30 seconds.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Interval between successive metric log lines.</summary>
    public TimeSpan MetricLogInterval { get; set; } = TimeSpan.FromSeconds(5);
}