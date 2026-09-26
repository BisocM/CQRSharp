using System.Diagnostics.Metrics;
using System.Threading.Channels;
using CQRSharp.Core.Diagnostics;

namespace CQRSharp.Core.BackgroundTasks;

/// <summary>
///     The instruments of one background task queue (<see cref="CqrsTelemetry.QueueInstruments" />), on a meter of its
///     own named <see cref="CqrsTelemetry.BackgroundTasksMeterName" />, created through the provider's
///     <see cref="IMeterFactory" /> when one is registered, as the CQRSharp meter is. A meter per queue keeps two hosts in
///     one process (tests, a host rebuilt on reload) from stopping each other's instruments, and lets the depth
///     instrument observe that queue's own channel. Like every CQRSharp instrument, nothing is measured until a listener
///     subscribes.
/// </summary>
internal sealed class BackgroundTaskQueueMetrics : IDisposable
{
    private readonly bool _ownsMeter;
    private readonly Counter<long> _enqueued;
    private readonly Counter<long> _evicted;
    private readonly Counter<long> _rejected;
    private readonly Histogram<double> _waitDuration;

    /// <summary>Creates the meter and its instruments.</summary>
    /// <param name="depth">Reads how many work items the queue holds right now.</param>
    /// <param name="meterFactory">The provider's meter factory, which then owns the meter; <c>null</c> for a meter of its own.</param>
    public BackgroundTaskQueueMetrics(Func<int> depth, IMeterFactory? meterFactory)
    {
        var options = new MeterOptions(CqrsTelemetry.BackgroundTasksMeterName) { Version = CqrsMetrics.Version };
        if (meterFactory is not null)
        {
            // The factory owns what it creates and disposes it with the provider.
            Meter = meterFactory.Create(options);
        }
        else
        {
            Meter = new Meter(options);
            _ownsMeter = true;
        }

        // An up-down counter rather than a gauge: depths add up across queues (two hosts in one process), and it is read
        // from the channel itself, so it cannot drift from the real backlog.
        Meter.CreateObservableUpDownCounter(CqrsTelemetry.QueueInstruments.Depth, depth, "{item}",
            "Work items waiting in the background task queue.");
        _enqueued = Meter.CreateCounter<long>(CqrsTelemetry.QueueInstruments.Enqueued, "{item}",
            "Work items the background task queue accepted.");
        _evicted = Meter.CreateCounter<long>(CqrsTelemetry.QueueInstruments.Evicted, "{item}",
            "Queued work items evicted from a full background task queue to make room for newer work.");
        _rejected = Meter.CreateCounter<long>(CqrsTelemetry.QueueInstruments.Rejected, "{item}",
            "Work items the background task queue refused.");
        _waitDuration = Meter.CreateHistogram<double>(CqrsTelemetry.QueueInstruments.WaitDuration, "s",
            "How long a work item waited in the background task queue before it started.");
    }

    /// <summary>The queue's meter.</summary>
    public Meter Meter { get; }

    /// <summary>Whether anything listens to the wait histogram, so the dequeue path reads the clock only when it is looked at.</summary>
    public bool WaitDurationEnabled => _waitDuration.Enabled;

    public void Enqueued() => _enqueued.Add(1);

    public void Evicted(BoundedChannelFullMode fullMode)
    {
        if (_evicted.Enabled)
            _evicted.Add(1, new KeyValuePair<string, object?>(CqrsTelemetry.Tags.QueueReason,
                fullMode == BoundedChannelFullMode.DropOldest ? "drop_oldest" : "drop_newest"));
    }

    public void Rejected(BackgroundTaskRejectionReason reason)
    {
        if (_rejected.Enabled)
            _rejected.Add(1, new KeyValuePair<string, object?>(CqrsTelemetry.Tags.QueueReason,
                reason == BackgroundTaskRejectionReason.QueueFull ? "full" : "closed"));
    }

    public void RecordWait(TimeSpan waited) => _waitDuration.Record(waited.TotalSeconds);

    public void Dispose()
    {
        if (_ownsMeter) Meter.Dispose();
    }
}
