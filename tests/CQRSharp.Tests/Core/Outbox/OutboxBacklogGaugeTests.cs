using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Persistence;
using FluentAssertions;
using static CQRSharp.Tests.Core.OutboxTestHarness;

namespace CQRSharp.Tests.Core;

/// <summary>
///     The backlog gauges report the last sample their provider's running processor took, and nothing once it stopped.
///     Each provider owns its gauges, so the tests read their own providers' meters only.
/// </summary>
public sealed class OutboxBacklogGaugeTests
{
    [Fact(DisplayName = "Outbox: the processor samples the backlog before it claims, the gauges report the sample, and a stopped processor's sample is cleared")]
    public async Task The_gauges_follow_the_running_processor()
    {
        ScriptedOutboxStore? store = null;
        var (provider, time, _) = BuildProbed(outbox: clock => store = new ScriptedOutboxStore(clock));
        await using var _ = provider;
        using var gauges = new GaugeReader(provider);
        store!.Backlog = new OutboxBacklog(7, 2, time.GetUtcNow().UtcDateTime.AddSeconds(-90));

        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await DrainAsync(provider);
            gauges.Read().Should().BeEquivalentTo(new Dictionary<string, double>
            {
                [CqrsTelemetry.Instruments.OutboxPending] = 7,
                [CqrsTelemetry.Instruments.OutboxDeadLetters] = 2,
                [CqrsTelemetry.Instruments.OutboxLag] = 90
            });
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        store.Calls.Should().StartWith(new[] { "backlog", "claim:0" }, "a shutdown during the sample must never leave a claimed batch behind");
        gauges.Read().Values.Should().HaveCount(3).And.OnlyContain(v => v == 0, "a stopped processor's last sample is not current");
    }

    [Fact(DisplayName = "Outbox: two hosts in one process each report their own backlog, and one stopping leaves the other's standing")]
    public async Task Each_host_reports_its_own_backlog()
    {
        ScriptedOutboxStore? firstStore = null;
        ScriptedOutboxStore? secondStore = null;
        var (first, firstTime, _) = BuildProbed(outbox: clock => firstStore = new ScriptedOutboxStore(clock));
        var (second, secondTime, _) = BuildProbed(outbox: clock => secondStore = new ScriptedOutboxStore(clock));
        await using var disposeFirst = first;
        await using var disposeSecond = second;
        using var firstGauges = new GaugeReader(first);
        using var secondGauges = new GaugeReader(second);
        firstStore!.Backlog = new OutboxBacklog(7, 2, firstTime.GetUtcNow().UtcDateTime.AddSeconds(-90));
        secondStore!.Backlog = new OutboxBacklog(3, 0, secondTime.GetUtcNow().UtcDateTime.AddSeconds(-30));

        var firstProcessor = Processor(first);
        var secondProcessor = Processor(second);
        await firstProcessor.StartAsync(CancellationToken.None);
        await secondProcessor.StartAsync(CancellationToken.None);
        try
        {
            await DrainAsync(first);
            await DrainAsync(second);
            firstGauges.Read()[CqrsTelemetry.Instruments.OutboxPending].Should().Be(7);
            secondGauges.Read()[CqrsTelemetry.Instruments.OutboxPending].Should().Be(3);

            await secondProcessor.StopAsync(CancellationToken.None);

            secondGauges.Read()[CqrsTelemetry.Instruments.OutboxPending].Should().Be(0);
            firstGauges.Read().Should().BeEquivalentTo(new Dictionary<string, double>
            {
                [CqrsTelemetry.Instruments.OutboxPending] = 7,
                [CqrsTelemetry.Instruments.OutboxDeadLetters] = 2,
                [CqrsTelemetry.Instruments.OutboxLag] = 90
            }, "the other host's processor stopping clears only its own sample");
        }
        finally
        {
            await firstProcessor.StopAsync(CancellationToken.None);
            await secondProcessor.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Listens to one provider's backlog gauges and reads them on demand.</summary>
    private sealed class GaugeReader : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly ConcurrentDictionary<string, double> _measurements = new();

        public GaugeReader(IServiceProvider provider)
        {
            var meter = ((CqrsMetrics)provider.GetService(typeof(CqrsMetrics))!).Meter;
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (ReferenceEquals(instrument.Meter, meter)
                        && instrument.Name is CqrsTelemetry.Instruments.OutboxPending or CqrsTelemetry.Instruments.OutboxDeadLetters or CqrsTelemetry.Instruments.OutboxLag)
                        l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => _measurements[instrument.Name] = value);
            _listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => _measurements[instrument.Name] = value);
            _listener.Start();
        }

        public Dictionary<string, double> Read()
        {
            _measurements.Clear();
            _listener.RecordObservableInstruments();
            return new Dictionary<string, double>(_measurements);
        }

        public void Dispose() => _listener.Dispose();
    }
}
