using System.Collections.Concurrent;
using System.Threading.Channels;
using CQRSharp.Core.BackgroundTasks.Types;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Notifications.Types;
using CQRSharp.Core.Options;
using CQRSharp.Shared.Data.Interfaces.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.BackgroundTasks;

/// <summary>
///     Bounded, thread‑safe queue for fire‑and‑forget background tasks.
///     <list type="bullet">
///         <item>Bounded capacity with four over‑flow strategies.</item>
///         <item>Per‑task completion/fault notification (optional).</item>
///         <item>Pushes <see cref="INotification" /> events on enqueue / reject.</item>
///         <item>Lightweight built‑in metrics if enabled.</item>
///     </list>
/// </summary>
internal sealed class BackgroundTaskQueue : IBackgroundTaskQueue
{
    //──────────────────────────────────────────────────────────────────────────
    //   Task‑completion tracking
    //    Keyed by <queue‑id, sequence>.  Static so that completion can be
    //    signalled from anywhere in the AppDomain, but collision‑free across
    //    multiple BackgroundTaskQueue instances.
    //──────────────────────────────────────────────────────────────────────────
    private static readonly ConcurrentDictionary<(Guid Q, long Seq), QueueTaskCompletionWrapper>
        TaskCompletions = new();

    private readonly Channel<QueuedTask> _channel;
    private readonly INotificationDispatcher _dispatcher;
    private readonly ILogger<BackgroundTaskQueue> _logger;
    private readonly Channel<INotification> _notificationChannel;
    private readonly SemaphoreSlim _notificationDispatchSemaphore;

    private readonly BackgroundTaskQueueOptions _options;
    //TODO: There is a potential memory leak here. If the host shuts down while some tasks remain in the channel, 
    //those entries won’t be removed. If someone decides to make an app where the host starts/restarts a lot, this will
    //obviously cause issues.

    /// <summary>Unique identity used in <see cref="TaskCompletions" />.</summary>
    private readonly Guid _queueId = Guid.NewGuid();

    private readonly CountingChannelReader _reader;
    private readonly CancellationToken _shutdownToken;
    private long _droppedNewestCount;
    private long _droppedOldestCount;
    private long _enqueuedCount;
    private DateTime _lastMetricLog = DateTime.UtcNow;
    private long _sequenceCounter;

    internal long CurrentCount; //updated by CountingChannelReader

    /// <summary>
    ///     Initializes a new instance of the <see cref="BackgroundTaskQueue" /> class
    ///     with the specified configuration, notification dispatcher and
    ///     infrastructure services.
    /// </summary>
    /// <param name="options">
    ///     The <see cref="IOptions{TOptions}" /> wrapper containing the
    ///     <see cref="BackgroundTaskQueueOptions" /> that govern queue capacity,
    ///     overflow policy, consumer‑count, metrics emission, notification retry
    ///     settings, and graceful‑shutdown time‑outs.
    /// </param>
    /// <param name="dispatcher">
    ///     Component responsible for publishing <see cref="INotification" /> events
    ///     (e.g.<c>TaskEnqueuedNotification</c>, <c>TaskRejectedNotification</c>)
    ///     that are emitted by the queue.  Must be non‑<see langword="null" />.
    /// </param>
    /// <param name="lifetime">
    ///     Provides access to the host’s <c>ApplicationStopping</c> token so the
    ///     queue can observe application‑wide shutdown and terminate its background
    ///     loops cleanly.
    /// </param>
    /// <param name="logger">
    ///     Logger used for internal diagnostics, error reporting, and—when
    ///     <c>EnableMetrics</c> is <see langword="true" />—periodic metric snapshots.
    /// </param>
    /// <exception cref="ArgumentNullException">
    ///     Thrown when any parameter is <see langword="null" />, or when
    ///     <paramref name="options" /> does not contain a value.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     Thrown when <c>options.Value.Capacity</c> is less than or equal to zero.
    /// </exception>
    /// <remarks>
    ///     The constructor performs the following steps:
    ///     <list type="number">
    ///         <item>Validates and stores constructor arguments.</item>
    ///         <item>
    ///             Builds the primary bounded <see cref="Channel{T}" /> that holds
    ///             <see cref="QueuedTask" /> instances, using the capacity and
    ///             <see cref="BoundedChannelFullMode" /> specified in
    ///             <see cref="BackgroundTaskQueueOptions" />.
    ///         </item>
    ///         <item>
    ///             Wraps the channel’s reader in a <see cref="CountingChannelReader" /> to
    ///             keep an atomic <c>CurrentCount</c> of live items.
    ///         </item>
    ///         <item>
    ///             Creates a secondary bounded channel for notification callbacks, sized
    ///             by <see cref="BackgroundTaskQueueOptions.CallbackChannelCapacity" />,
    ///             which drops the oldest message when full to avoid dead‑lock.
    ///         </item>
    ///         <item>
    ///             Instantiates a <see cref="SemaphoreSlim" /> to throttle concurrent
    ///             notification dispatch to the number of logical processors.
    ///         </item>
    ///         <item>
    ///             If metrics are enabled, logs an informational message describing the
    ///             configured capacity.
    ///         </item>
    ///         <item>
    ///             Starts an asynchronous, fire‑and‑forget loop that drains the
    ///             notification channel until the host begins shutting down.
    ///         </item>
    ///     </list>
    /// </remarks>
    public BackgroundTaskQueue(
        IOptions<BackgroundTaskQueueOptions> options,
        INotificationDispatcher dispatcher,
        IHostApplicationLifetime lifetime,
        ILogger<BackgroundTaskQueue> logger)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _shutdownToken = lifetime.ApplicationStopping;

        if (_options.Capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(_options.Capacity), "Capacity must be > 0.");

        //Primary bounded channel for work items
        var chanOpts = new BoundedChannelOptions(_options.Capacity)
        {
            FullMode = _options.FullMode,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };
        _channel = Channel.CreateBounded<QueuedTask>(chanOpts);
        _reader = new CountingChannelReader(_channel.Reader, this);

        //Secondary channel for enqueue / reject notifications
        var notifOpts = new BoundedChannelOptions(_options.CallbackChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };
        _notificationChannel = Channel.CreateBounded<INotification>(notifOpts);

        //Throttle parallel notification dispatch to CPU count
        _notificationDispatchSemaphore =
            new SemaphoreSlim(Environment.ProcessorCount, Environment.ProcessorCount);

        if (_options.EnableMetrics)
            _logger.LogInformation("BackgroundTaskQueue metrics ENABLED (capacity = {Cap}).",
                _options.Capacity);

        //Fire‑and‑forget background loop for notifications
        _ = ProcessNotificationsAsync(_shutdownToken);
    }

    /// <inheritdoc />
    public long TotalItemsEnqueued => Interlocked.Read(ref _enqueuedCount);

    /// <inheritdoc />
    public long TotalDroppedNewest => Interlocked.Read(ref _droppedNewestCount);

    /// <inheritdoc />
    public long TotalDroppedOldest => Interlocked.Read(ref _droppedOldestCount);

    /// <inheritdoc />
    public ChannelReader<QueuedTask> Reader => _reader;

    /// <inheritdoc />
    public async Task<QueueWriteResult> QueueBackgroundWorkItemAsync(
        Func<CancellationToken, Task> workItem,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        var seq = Interlocked.Increment(ref _sequenceCounter);
        var qt = new QueuedTask(_queueId, seq, workItem, DateTime.UtcNow);
        var writer = _channel.Writer;

        QueueWriteResultCode code;

        switch (_options.FullMode)
        {
            case BoundedChannelFullMode.DropNewest:
                if (!writer.TryWrite(qt))
                {
                    Interlocked.Increment(ref _droppedNewestCount);
                    EnqueueNotification(new TaskRejectedNotification(seq, _options.FullMode));
                    code = QueueWriteResultCode.DroppedNewest;
                }
                else
                {
                    PostWriteBookKeeping(seq, workItem);
                    code = QueueWriteResultCode.Enqueued;
                }

                break;

            //TODO: The way this is made at the moment, it permits a race condition.
            //this is a known risk in lock‑free bounded buffers. If a need for stronger synchronization
            //ever arises, we will need to lock both drop and enqueue logic in one lock.
            case BoundedChannelFullMode.DropOldest:
            {
                var droppedSomeone = false;

                //remove oldest *first* (reader.TryRead triggers CurrentCount--)
                if (Interlocked.Read(ref CurrentCount) >= _options.Capacity &&
                    _reader.TryRead(out var droppedTask))
                {
                    droppedSomeone = true;
                    Interlocked.Increment(ref _droppedOldestCount);

                    //cancel any awaiting TCS
                    TryCancelTaskCompletion(droppedTask.QueueId, droppedTask.SequenceNumber);

                    EnqueueNotification(new TaskRejectedNotification(
                        droppedTask.SequenceNumber, _options.FullMode));
                }

                //now definitely write the new task
                await writer.WriteAsync(qt, cancellationToken).ConfigureAwait(false);
                PostWriteBookKeeping(seq, workItem);

                code = droppedSomeone
                    ? QueueWriteResultCode.DroppedOldest
                    : QueueWriteResultCode.Enqueued;
            }
                break;

            case BoundedChannelFullMode.DropWrite:
                //In DropWrite mode, TryWrite returns true iff the item was enqueued.
                if (writer.TryWrite(qt))
                {
                    //Actually enqueued: update counters and emit notification.
                    PostWriteBookKeeping(seq, workItem);
                    code = QueueWriteResultCode.Enqueued;
                }
                else
                {
                    //Dropped because the channel was full.
                    Interlocked.Increment(ref _droppedNewestCount);
                    EnqueueNotification(new TaskRejectedNotification(seq, _options.FullMode));
                    code = QueueWriteResultCode.DroppedNewest;
                }

                break;

            case BoundedChannelFullMode.Wait:
            default:
                if (writer.TryWrite(qt))
                {
                    PostWriteBookKeeping(seq, workItem);
                    code = QueueWriteResultCode.Enqueued;
                }
                else
                {
                    await writer.WriteAsync(qt, cancellationToken).ConfigureAwait(false);
                    PostWriteBookKeeping(seq, workItem);
                    code = QueueWriteResultCode.Waited;
                }

                break;
        }

        MaybeLogMetrics();
        return new QueueWriteResult(code, seq);
    }

    /// <inheritdoc />
    public ValueTask<QueuedTask> DequeueAsync(CancellationToken token)
    {
        return _reader.ReadAsync(token);
    }

    private void PostWriteBookKeeping(
        long seq,
        Func<CancellationToken, Task> workItem)
    {
        Interlocked.Increment(ref _enqueuedCount);
        Interlocked.Increment(ref CurrentCount);

        EnqueueNotification(new TaskEnqueuedNotification(seq, workItem));
    }

    private void MaybeLogMetrics()
    {
        if (!_options.EnableMetrics) return;

        var now = DateTime.UtcNow;
        if (now - _lastMetricLog < _options.MetricLogInterval) return;

        _lastMetricLog = now;
        _logger.LogInformation("Metrics ⟶ Enqueued:{Enq}  DroppedNew:{DN}  DroppedOld:{DO}  Live:{Live}",
            TotalItemsEnqueued,
            TotalDroppedNewest,
            TotalDroppedOldest,
            Interlocked.Read(ref CurrentCount));
    }

    private static void TryCancelTaskCompletion(Guid qid, long seq)
    {
        if (TaskCompletions.TryRemove((qid, seq), out var wrapper))
            wrapper.CancelAction();
    }

    private void EnqueueNotification(INotification notification)
    {
        //fire‑and‑forget; swallow any exception and log
        _ = WriteNotificationAsync(notification).ContinueWith(
            static (t, state) =>
            {
                if (!t.IsFaulted || t.Exception is null) return;
                //the state object is the logger we supplied below
                var log = (ILogger<BackgroundTaskQueue>)state!;
                log.LogError(t.Exception,
                    "Failed to enqueue notification.");
            },
            _logger, //state object
            TaskScheduler.Default); //choose scheduler as before
    }

    private async Task WriteNotificationAsync(INotification notif)
    {
        try
        {
            await _notificationChannel.Writer
                .WriteAsync(notif, _shutdownToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Notification enqueue aborted by shutdown.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while enqueueing notification.");
        }
    }

    private async Task ProcessNotificationsAsync(CancellationToken token)
    {
        var reader = _notificationChannel.Reader;

        try
        {
            while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
            while (reader.TryRead(out var notif))
            {
                await _notificationDispatchSemaphore.WaitAsync(token).ConfigureAwait(false);

                //Run on ThreadPool but limit parallelism by semaphore only
                _ = Task.Run(async () =>
                {
                    try
                    {
                        for (var attempt = 1;; attempt++)
                            try
                            {
                                await _dispatcher.Publish(notif, CancellationToken.None)
                                    .ConfigureAwait(false);
                                return;
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex) when (attempt < _options.NotificationMaxRetries)
                            {
                                _logger.LogWarning(ex,
                                    "Publish attempt {Attempt} for {Notif} failed; retrying…",
                                    attempt, notif.GetType().Name);

                                await Task.Delay(_options.NotificationRetryDelay, token)
                                    .ConfigureAwait(false);
                            }
                    }
                    finally
                    {
                        _notificationDispatchSemaphore.Release();
                    }
                }, token);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Notification loop cancelled during shutdown.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error inside notification loop.");
        }
    }

    internal void RegisterTaskCompletion(
        long seq,
        Action cancel,
        Action<Exception> fault)
    {
        TaskCompletions[(_queueId, seq)] =
            new QueueTaskCompletionWrapper(cancel, fault);
    }

    internal static void CompleteTaskAsRan(Guid qid, long seq)
    {
        TaskCompletions.TryRemove((qid, seq), out _);
    }

    internal static void SignalTaskException(Guid qid, long seq, Exception ex)
    {
        if (TaskCompletions.TryRemove((qid, seq), out var wrapper))
            wrapper.ExceptionAction(ex);
    }
}