using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using CQRSharp.Core.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     The dispatch instruments on the <c>CQRSharp</c> meter. A <see cref="MeterListener" /> is process-wide, so each
///     test filters on its own fixture's request type; other tests running in parallel simply take the metered path.
/// </summary>
public sealed class CqrsMetricsTests
{
    [Fact(DisplayName = "A dispatched command records cqrsharp.request.duration tagged with its type, kind and outcome")]
    public async Task Request_duration_is_recorded()
    {
        var recorded = new ConcurrentBag<(double Seconds, string Kind, string Outcome)>();
        using var listener = Listen(CqrsTelemetry.Instruments.RequestDuration, nameof(MeteredCommand), tags =>
            recorded.Add((tags.Value, (string)tags.Tags["cqrsharp.request.kind"]!, (string)tags.Tags["cqrsharp.outcome"]!)));

        await using var provider = new ServiceCollection().AddCqrsGenerated().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        await dispatcher.Send(new MeteredCommand());
        await dispatcher.Send(new MeteredCommand { Fail = true });
        var thrown = () => dispatcher.Send(new MeteredCommand { Throw = true });
        await thrown.Should().ThrowAsync<InvalidOperationException>();

        recorded.Should().HaveCount(3);
        recorded.Should().OnlyContain(r => r.Kind == "command" && r.Seconds >= 0);
        recorded.Count(r => r.Outcome == "success").Should().Be(1);
        recorded.Count(r => r.Outcome == "failure").Should().Be(2, "a returned failed result and a thrown exception are both failures");
    }

    [Fact(DisplayName = "The published names match what the instruments are actually called")]
    public void Published_names_are_the_real_names()
    {
        CqrsTelemetry.MeterNames.Should().Contain([CqrsTelemetry.MeterName, CqrsTelemetry.BackgroundTasksMeterName]);
        CqrsTelemetry.ActivitySourceNames.Should().Contain([CqrsActivitySource.Name, CqrsTelemetry.PipelinesActivitySourceName]);

        var instruments = new ConcurrentBag<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == CqrsTelemetry.MeterName) instruments.Add(instrument.Name);
            }
        };
        listener.Start();

        instruments.Should().Contain([
            CqrsTelemetry.Instruments.RequestDuration,
            CqrsTelemetry.Instruments.NotificationsPublished,
            CqrsTelemetry.Instruments.OutboxMessages,
            CqrsTelemetry.Instruments.OutboxDispatchDuration
        ]);
    }

    private static MeterListener Listen(string instrumentName, string requestTypeName, Action<(double Value, Dictionary<string, object?> Tags)> onMeasurement)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == CqrsTelemetry.MeterName && instrument.Name == instrumentName)
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            var map = new Dictionary<string, object?>();
            foreach (var tag in tags) map[tag.Key] = tag.Value;
            if (Equals(map.GetValueOrDefault("cqrsharp.request.type"), requestTypeName))
                onMeasurement((value, map));
        });
        listener.Start();
        return listener;
    }
}

public sealed class MeteredCommand : CommandBase
{
    public bool Fail { get; init; }
    public bool Throw { get; init; }
}

public sealed class MeteredCommandHandler : ICommandHandler<MeteredCommand>
{
    public Task<CommandResult> Handle(MeteredCommand command, CancellationToken cancellationToken)
    {
        if (command.Throw) throw new InvalidOperationException("metered boom");
        return Task.FromResult(command.Fail ? CommandResult.FromError("nope") : CommandResult.FromSuccess());
    }
}
