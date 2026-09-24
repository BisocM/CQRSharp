using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using CQRSharp.Core.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     The instruments on the <c>CQRSharp</c> meter. Each service provider owns its own meter, so every test listens to
///     its own provider's instruments only and is unaffected by the providers of tests running in parallel.
/// </summary>
public sealed class CqrsMetricsTests
{
    [Fact(DisplayName = "A dispatched command records cqrsharp.request.duration tagged with its type, kind and outcome")]
    public async Task Request_duration_is_recorded()
    {
        await using var provider = new ServiceCollection().AddCqrsGenerated().BuildServiceProvider();
        using var durations = new InstrumentRecorder<double>(provider, CqrsTelemetry.Instruments.RequestDuration);
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        await dispatcher.Send(new MeteredCommand(), TestContext.Current.CancellationToken);
        await dispatcher.Send(new MeteredCommand { Fail = true }, TestContext.Current.CancellationToken);
        var thrown = () => dispatcher.Send(new MeteredCommand { Throw = true });
        await thrown.Should().ThrowAsync<InvalidOperationException>();

        durations.Measurements.Should().HaveCount(3).And.OnlyContain(m =>
            m.Value >= 0 &&
            m.Tag(CqrsTelemetry.Tags.RequestType) == typeof(MeteredCommand).ToString() &&
            m.Tag(CqrsTelemetry.Tags.RequestKind) == "command");
        durations.Measurements.Select(m => m.Tag(CqrsTelemetry.Tags.Outcome)).Should()
            .BeEquivalentTo(["success", "failure", "failure"], "a returned failed result and a thrown exception are both failures");
    }

    [Fact(DisplayName = "Queries are recorded as kind query and value-returning commands as kind command, each with its outcome")]
    public async Task Request_kinds_are_recorded()
    {
        await using var provider = new ServiceCollection().AddCqrsGenerated().BuildServiceProvider();
        using var durations = new InstrumentRecorder<double>(provider, CqrsTelemetry.Instruments.RequestDuration);
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        await dispatcher.Send(new MeteredQuery(), TestContext.Current.CancellationToken);
        var thrown = () => dispatcher.Send(new MeteredQuery { Throw = true });
        await thrown.Should().ThrowAsync<InvalidOperationException>();
        await dispatcher.Send(new MeteredValueCommand(), TestContext.Current.CancellationToken);

        durations.Measurements.Select(m => $"{m.Tag(CqrsTelemetry.Tags.RequestType)} {m.Tag(CqrsTelemetry.Tags.RequestKind)} {m.Tag(CqrsTelemetry.Tags.Outcome)}")
            .Should().BeEquivalentTo([
                $"{typeof(MeteredQuery)} query success",
                $"{typeof(MeteredQuery)} query failure",
                $"{typeof(MeteredValueCommand)} command success"
            ]);
    }

    [Fact(DisplayName = "A stream is recorded as kind stream: a success once enumerated to its end, a failure when it faults or its consumer stops early")]
    public async Task Stream_outcomes_are_recorded()
    {
        await using var provider = new ServiceCollection().AddCqrsGenerated().BuildServiceProvider();
        using var durations = new InstrumentRecorder<double>(provider, CqrsTelemetry.Instruments.RequestDuration);
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        await foreach (var _ in dispatcher.Stream(new MeteredStream(), TestContext.Current.CancellationToken))
        {
        }

        await foreach (var _ in dispatcher.Stream(new MeteredStream(), TestContext.Current.CancellationToken))
            break;

        var faulting = async () =>
        {
            await foreach (var _ in dispatcher.Stream(new MeteredStream { Throw = true }))
            {
            }
        };
        await faulting.Should().ThrowAsync<InvalidOperationException>();

        durations.Measurements.Should().OnlyContain(m =>
            m.Tag(CqrsTelemetry.Tags.RequestType) == typeof(MeteredStream).ToString() &&
            m.Tag(CqrsTelemetry.Tags.RequestKind) == "stream");
        durations.Measurements.Select(m => m.Tag(CqrsTelemetry.Tags.Outcome)).Should()
            .Equal(["success", "failure", "failure"], "each stream is recorded once it ends, in the order they ended");
    }

    [Fact(DisplayName = "Request types that share a short name are recorded as separate series")]
    public async Task Request_types_are_told_apart_by_their_full_name()
    {
        await using var provider = new ServiceCollection().AddCqrsGenerated().BuildServiceProvider();
        using var durations = new InstrumentRecorder<double>(provider, CqrsTelemetry.Instruments.RequestDuration);
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        await dispatcher.Send(new PlaceOrder.Command(), TestContext.Current.CancellationToken);
        await dispatcher.Send(new CancelOrder.Command(), TestContext.Current.CancellationToken);

        durations.Measurements.Select(m => m.Tag(CqrsTelemetry.Tags.RequestType)).Should()
            .BeEquivalentTo([typeof(PlaceOrder.Command).ToString(), typeof(CancelOrder.Command).ToString()]);
    }

    [Fact(DisplayName = "A notification published in-process is counted under its full type name")]
    public async Task Published_notifications_are_counted()
    {
        await using var provider = new ServiceCollection().AddCqrsGenerated().BuildServiceProvider();
        using var published = new InstrumentRecorder<long>(provider, CqrsTelemetry.Instruments.NotificationsPublished);
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish(new MeteredNotification(), TestContext.Current.CancellationToken);

        published.Measurements.Should().ContainSingle().Which.Should().Match<InstrumentRecorder<long>.Measurement>(m =>
            m.Value == 1 && m.Tag(CqrsTelemetry.Tags.NotificationType) == typeof(MeteredNotification).ToString());
    }

    [Fact(DisplayName = "A delivered outbox message is counted and timed under its notification name, handler and outcome")]
    public async Task Outbox_deliveries_are_measured()
    {
        var (provider, _, _) = OutboxTestHarness.BuildProbed();
        await using var disposeProvider = provider;
        using var messages = new InstrumentRecorder<long>(provider, CqrsTelemetry.Instruments.OutboxMessages);
        using var durations = new InstrumentRecorder<double>(provider, CqrsTelemetry.Instruments.OutboxDispatchDuration);
        await OutboxTestHarness.PublishAsync(provider, new ParallelProbe(1, null));
        var handlerName = OutboxTestHarness.Stored(provider).Should().ContainSingle().Subject.HandlerName;

        var processor = OutboxTestHarness.Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await OutboxTestHarness.DrainAsync(provider);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        var expectedTags = new Dictionary<string, object?>
        {
            [CqrsTelemetry.Tags.NotificationName] = "tests.parallel.probe",
            [CqrsTelemetry.Tags.NotificationHandler] = handlerName,
            [CqrsTelemetry.Tags.Outcome] = "processed"
        };
        messages.Measurements.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Value = 1L, Tags = expectedTags });
        durations.Measurements.Should().ContainSingle().Which.Tags.Should().BeEquivalentTo(expectedTags);
    }

    [Fact(DisplayName = "The published names are what the instruments are called, and each provider publishes its own")]
    public async Task Published_names_are_the_real_names()
    {
        CqrsTelemetry.MeterNames.Should().Contain([CqrsTelemetry.MeterName, CqrsTelemetry.BackgroundTasksMeterName]);
        CqrsTelemetry.ActivitySourceNames.Should().Contain([CqrsTelemetry.ActivitySourceName, CqrsTelemetry.PipelinesActivitySourceName]);

        await using var first = new ServiceCollection().AddCqrsGenerated().BuildServiceProvider();
        await using var second = new ServiceCollection().AddCqrsGenerated().BuildServiceProvider();
        var firstMeter = first.GetRequiredService<CqrsMetrics>().Meter;
        var secondMeter = second.GetRequiredService<CqrsMetrics>().Meter;
        firstMeter.Should().NotBeSameAs(secondMeter);

        var instruments = new ConcurrentBag<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (ReferenceEquals(instrument.Meter, firstMeter)) instruments.Add(instrument.Name);
            }
        };
        listener.Start();

        firstMeter.Name.Should().Be(CqrsTelemetry.MeterName);
        instruments.Should().BeEquivalentTo([
            CqrsTelemetry.Instruments.RequestDuration,
            CqrsTelemetry.Instruments.NotificationsPublished,
            CqrsTelemetry.Instruments.OutboxMessages,
            CqrsTelemetry.Instruments.OutboxDispatchDuration,
            CqrsTelemetry.Instruments.OutboxPending,
            CqrsTelemetry.Instruments.OutboxDeadLetters,
            CqrsTelemetry.Instruments.OutboxLag
        ]);
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

public sealed record MeteredNotification : INotification;

// Vertical slices name their requests alike: only the full name tells them apart.
public static class PlaceOrder
{
    public sealed class Command : CommandBase;

    public sealed class Handler : ICommandHandler<Command>
    {
        public Task<CommandResult> Handle(Command command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
    }
}

public static class CancelOrder
{
    public sealed class Command : CommandBase;

    public sealed class Handler : ICommandHandler<Command>
    {
        public Task<CommandResult> Handle(Command command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
    }
}

public sealed class MeteredQuery : QueryBase<int>
{
    public bool Throw { get; init; }
}

public sealed class MeteredQueryHandler : IQueryHandler<MeteredQuery, int>
{
    public Task<int> Handle(MeteredQuery query, CancellationToken cancellationToken)
        => query.Throw ? throw new InvalidOperationException("metered query boom") : Task.FromResult(1);
}

public sealed class MeteredValueCommand : ResultCommandBase<int>;

public sealed class MeteredValueCommandHandler : IResultCommandHandler<MeteredValueCommand, int>
{
    public Task<CommandResult<int>> Handle(MeteredValueCommand command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult<int>.FromSuccess(1));
}

public sealed class MeteredStream : StreamRequestBase<int>
{
    public bool Throw { get; init; }
}

public sealed class MeteredStreamHandler : IStreamRequestHandler<MeteredStream, int>
{
    public async IAsyncEnumerable<int> Handle(MeteredStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 1;
        await Task.Yield();
        if (request.Throw) throw new InvalidOperationException("metered stream boom");
        yield return 2;
    }
}
