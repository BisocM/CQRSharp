using System.Threading.Channels;

namespace CQRSharp;

/// <summary>
///     Configuration options for the background task queue behind <see cref="RunMode.Queued" /> dispatch and
///     <see cref="CQRSharp.Core.BackgroundTasks.IBackgroundTaskManager" />.
/// </summary>
public sealed class BackgroundTaskQueueOptions
{
    /// <summary>
    ///     Maximum number of work items the queue holds waiting to run; work that is already running does not count.
    ///     Must be greater than zero, or the options fail validation when the host starts. Default is 1000.
    /// </summary>
    public int Capacity { get; set; } = 1000;

    /// <summary>
    ///     What happens when a work item arrives and the queue already holds <see cref="Capacity" /> items. A work item
    ///     the queue does not run faults its caller's task with <see cref="BackgroundTaskRejectedException" />.
    ///     <list type="bullet">
    ///         <item>
    ///             <see cref="BoundedChannelFullMode.Wait" /> (default): the caller waits for room. Its cancellation token
    ///             ends the wait.
    ///         </item>
    ///         <item>
    ///             <see cref="BoundedChannelFullMode.DropWrite" />: the new work item is refused
    ///             (<see cref="BackgroundTaskRejectionReason.QueueFull" />).
    ///         </item>
    ///         <item>
    ///             <see cref="BoundedChannelFullMode.DropOldest" />: the oldest queued item is evicted to make room
    ///             (<see cref="BackgroundTaskRejectionReason.Evicted" />).
    ///         </item>
    ///         <item>
    ///             <see cref="BoundedChannelFullMode.DropNewest" />: the newest queued item is evicted to make room
    ///             (<see cref="BackgroundTaskRejectionReason.Evicted" />).
    ///         </item>
    ///     </list>
    /// </summary>
    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.Wait;

    /// <summary>
    ///     Maximum number of work items that run at the same time. Each runs on the thread pool, so a work item that blocks
    ///     holds a pool thread, not the queue. Defaults to <c>Environment.ProcessorCount</c>; zero or negative uses the
    ///     default.
    /// </summary>
    public int ConsumerCount { get; set; } = Environment.ProcessorCount;

    /// <summary>
    ///     When the host stops, how long the work still running — and, with <see cref="DrainOnShutdown" />, the work still
    ///     queued — gets to finish before it is cancelled. The host's own <c>HostOptions.ShutdownTimeout</c> (30 seconds by
    ///     default) caps it too: once that runs out the remaining work is cancelled at once. The host's budget is shared by
    ///     every hosted service, so keep this one clearly below it, leaving time for the services the host stops before the
    ///     queue (the web server, the outbox processor) and after it (hosted services registered before CQRSharp). Default
    ///     is 20 seconds.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    ///     Whether shutdown also runs the work that is still <em>queued</em>, not just what is already executing. The
    ///     queue stops accepting new work the moment shutdown begins; whatever <see cref="ShutdownTimeout" /> does not
    ///     cover is cancelled, so nothing awaiting a queued item hangs. Set to <see langword="false" /> to cancel queued
    ///     work immediately instead. Default is <see langword="true" />.
    /// </summary>
    public bool DrainOnShutdown { get; set; } = true;

    /// <summary>
    ///     How long a <see cref="CQRSharp.RunMode.Queued" /> dispatch waits for the background queue consumer to start
    ///     before failing. The consumer only runs once the Generic Host has started its hosted services; without a
    ///     running host nothing drains the queue, so a queued dispatch would otherwise hang forever. After this timeout
    ///     CQRSharp throws a clear error instead. Default is 10 seconds. (Irrelevant under the default
    ///     <see cref="CQRSharp.RunMode.Inline" />, where nothing is queued.)
    /// </summary>
    public TimeSpan ConsumerStartTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
