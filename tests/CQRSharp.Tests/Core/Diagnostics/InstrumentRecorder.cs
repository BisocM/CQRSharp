using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using CQRSharp.Core.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Records what one service provider's CQRSharp instrument measures. Each provider owns its meter, so measurements
///     from the providers of tests running in parallel never arrive here, and subscribing switches measuring on for this
///     provider alone: its dispatches take the measured path, everyone else's do not.
/// </summary>
internal sealed class InstrumentRecorder<T> : IDisposable where T : struct
{
    private readonly MeterListener _listener;
    private readonly ConcurrentQueue<Measurement> _measurements = new();

    public InstrumentRecorder(IServiceProvider provider, string instrumentName)
    {
        var meter = provider.GetRequiredService<CqrsMetrics>().Meter;
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == instrumentName)
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<T>((_, value, tags, _) =>
        {
            var map = new Dictionary<string, object?>();
            foreach (var tag in tags) map[tag.Key] = tag.Value;
            _measurements.Enqueue(new Measurement(value, map));
        });
        _listener.Start();
    }

    public IReadOnlyList<Measurement> Measurements => _measurements.ToArray();

    public void Dispose() => _listener.Dispose();

    public sealed record Measurement(T Value, IReadOnlyDictionary<string, object?> Tags)
    {
        public string? Tag(string key) => Tags.TryGetValue(key, out var value) ? value as string : null;
    }
}
