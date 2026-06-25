using System.Diagnostics.Metrics;
using CQRSharp.Core.Background.TaskQueue.Telemetry;
using FluentAssertions;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Guards against the process-wide meter teardown: each reporter must own its meter, so disposing one (e.g. a host
///     being rebuilt within the same process) does not silently stop metrics for every other instance.
/// </summary>
public sealed class OpenTelemetryQueueMetricsReporterTests
{
    [Fact(DisplayName = "Disposing one reporter does not stop metrics on another instance")]
    public void DisposingOneReporter_DoesNotKillMetricsProcessWide()
    {
        var captured = new List<long>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "CQRSharp.Core.BackgroundTasks" &&
                    instrument.Name == "cqrsharp.queue.items.enqueued.total")
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
        {
            lock (captured) captured.Add(value);
        });
        listener.Start();

        // Two independent reporters, as a host rebuild or several tests in one process would create.
        var first = new OpenTelemetryQueueMetricsReporter();
        var second = new OpenTelemetryQueueMetricsReporter();

        // Disposing the first must tear down only its own meter.
        first.Dispose();

        // The second instance's instruments must still publish measurements.
        second.ItemEnqueued();

        captured.Should().Contain(1, "disposing one reporter must not stop metrics for any other instance");

        second.Dispose();
    }
}
