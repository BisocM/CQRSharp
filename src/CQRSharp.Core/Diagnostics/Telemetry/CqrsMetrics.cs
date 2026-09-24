using System.Diagnostics;
using System.Diagnostics.Metrics;
using CQRSharp.Persistence;

namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     The instruments behind <see cref="CqrsTelemetry.Instruments" />, owned by one service provider: its own
///     <see cref="CqrsTelemetry.MeterName" /> meter, created through the provider's <see cref="IMeterFactory" /> when one
///     is registered. Two hosts in one process each report their own backlog, and one stopping never clears the
///     other's. Each call site checks an instrument's <c>Enabled</c> first, so nothing is measured until a listener
///     subscribes.
/// </summary>
internal sealed class CqrsMetrics : IDisposable
{
    /// <summary>The version the CQRSharp meter and activity source report: the Core assembly's.</summary>
    public static readonly string? Version = typeof(CqrsMetrics).Assembly.GetName().Version?.ToString(3);

    private readonly bool _ownsMeter;
    private readonly TimeProvider _clock;
    private readonly ObservableGauge<long> _outboxPending;
    private readonly ObservableGauge<long> _outboxDeadLetters;
    private readonly ObservableGauge<double> _outboxLag;
    private readonly object _backlogGate = new();
    private OutboxBacklog? _backlog;

    public CqrsMetrics(TimeProvider? timeProvider = null, IMeterFactory? meterFactory = null)
    {
        _clock = timeProvider ?? TimeProvider.System;

        var options = new MeterOptions(CqrsTelemetry.MeterName) { Version = Version };
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

        RequestDuration = Meter.CreateHistogram<double>(
            CqrsTelemetry.Instruments.RequestDuration, "s", "Duration of a dispatched command, query or stream.");
        NotificationsPublished = Meter.CreateCounter<long>(
            CqrsTelemetry.Instruments.NotificationsPublished, "{notification}", "Notifications published in-process.");
        OutboxMessages = Meter.CreateCounter<long>(
            CqrsTelemetry.Instruments.OutboxMessages, "{message}", "Outbox messages the processor finished an attempt at, by outcome.");
        OutboxDispatchDuration = Meter.CreateHistogram<double>(
            CqrsTelemetry.Instruments.OutboxDispatchDuration, "s", "Duration of dispatching one outbox message to its handler.");

        // Observable: the collector pulls them, so they report the processor's last backlog sample rather than the store.
        _outboxPending = Meter.CreateObservableGauge(
            CqrsTelemetry.Instruments.OutboxPending, () => Backlog?.PendingCount ?? 0, "{message}", "Outbox messages still to be delivered.");
        _outboxDeadLetters = Meter.CreateObservableGauge(
            CqrsTelemetry.Instruments.OutboxDeadLetters, () => Backlog?.DeadLetterCount ?? 0, "{message}", "Dead-lettered outbox messages.");
        _outboxLag = Meter.CreateObservableGauge(
            CqrsTelemetry.Instruments.OutboxLag, () => Backlog?.LagAt(_clock.GetUtcNow().UtcDateTime).TotalSeconds ?? 0, "s",
            "Age of the oldest undelivered outbox message.");
    }

    /// <summary>This provider's CQRSharp meter.</summary>
    public Meter Meter { get; }

    public Histogram<double> RequestDuration { get; }

    public Counter<long> NotificationsPublished { get; }

    public Counter<long> OutboxMessages { get; }

    public Histogram<double> OutboxDispatchDuration { get; }

    /// <summary>Whether anything listens to the backlog gauges, so the processor samples the backlog only when it is looked at.</summary>
    public bool OutboxBacklogEnabled => _outboxPending.Enabled || _outboxDeadLetters.Enabled || _outboxLag.Enabled;

    private OutboxBacklog? Backlog
    {
        get
        {
            lock (_backlogGate) return _backlog;
        }
    }

    public void RecordRequest(Type requestType, string kind, bool succeeded, TimeSpan elapsed)
    {
        var tags = new TagList
        {
            { CqrsTelemetry.Tags.RequestType, CqrsTelemetry.TypeName(requestType) },
            { CqrsTelemetry.Tags.RequestKind, kind },
            { CqrsTelemetry.Tags.Outcome, succeeded ? "success" : "failure" }
        };
        RequestDuration.Record(elapsed.TotalSeconds, tags);
    }

    public void RecordNotificationPublished(Type notificationType)
        => NotificationsPublished.Add(1, new KeyValuePair<string, object?>(CqrsTelemetry.Tags.NotificationType, CqrsTelemetry.TypeName(notificationType)));

    public void RecordOutbox(string notificationName, string handlerName, string outcome, TimeSpan elapsed)
    {
        var tags = new TagList
        {
            { CqrsTelemetry.Tags.NotificationName, notificationName },
            { CqrsTelemetry.Tags.NotificationHandler, handlerName },
            { CqrsTelemetry.Tags.Outcome, outcome }
        };
        OutboxMessages.Add(1, tags);
        OutboxDispatchDuration.Record(elapsed.TotalSeconds, tags);
    }

    /// <summary>Records the processor's latest backlog sample, which the gauges report until the next one.</summary>
    public void RecordBacklog(OutboxBacklog backlog)
    {
        lock (_backlogGate) _backlog = backlog;
    }

    /// <summary>Forgets the last backlog sample, so a stopped processor's backlog is not reported as if it were current.</summary>
    public void ClearBacklog()
    {
        lock (_backlogGate) _backlog = null;
    }

    public void Dispose()
    {
        if (_ownsMeter) Meter.Dispose();
    }
}
