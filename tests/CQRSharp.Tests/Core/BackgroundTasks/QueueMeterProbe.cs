using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using CQRSharp.Core.BackgroundTasks;
using CQRSharp.Core.Diagnostics;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Listens to one queue's instruments only. A <see cref="MeterListener" /> is process-wide and every queue's meter
///     has the same name, so the probe filters on the queue's own <see cref="Meter" /> instance: tests running in
///     parallel never see each other's measurements.
/// </summary>
internal sealed class QueueMeterProbe : IDisposable
{
    private readonly ConcurrentDictionary<string, byte> _instruments = new();
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<(string Instrument, double Value, string? Reason)> _measurements = new();
    private int _depth = -1;

    public QueueMeterProbe(Meter meter)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (!ReferenceEquals(instrument.Meter, meter)) return;
            _instruments.TryAdd(instrument.Name, 0);
            listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<int>((instrument, value, _, _) =>
        {
            if (instrument.Name == CqrsTelemetry.QueueInstruments.Depth)
                Volatile.Write(ref _depth, value);
        });
        _listener.Start();
    }

    /// <summary>The names of the instruments the probe listens to.</summary>
    public IReadOnlyCollection<string> Instruments => _instruments.Keys.ToArray();

    /// <summary>The depth the queue reports right now.</summary>
    public int Depth()
    {
        _listener.RecordObservableInstruments();
        return Volatile.Read(ref _depth);
    }

    /// <summary>The sum of a counter's measurements, optionally only those tagged with <paramref name="reason" />.</summary>
    public long Count(string instrument, string? reason = null)
        => (long)_measurements.Where(m => m.Instrument == instrument && (reason is null || m.Reason == reason)).Sum(m => m.Value);

    /// <summary>Every value a histogram recorded.</summary>
    public IReadOnlyList<double> Values(string instrument)
        => _measurements.Where(m => m.Instrument == instrument).Select(m => m.Value).ToList();

    public void Dispose() => _listener.Dispose();

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        string? reason = null;
        foreach (var tag in tags)
            if (tag.Key == CqrsTelemetry.Tags.QueueReason)
                reason = tag.Value as string;

        _measurements.Enqueue((instrument.Name, value, reason));
    }
}
