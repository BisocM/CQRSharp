using System.Collections.Concurrent;
using CQRSharp.Core.Outbox;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static CQRSharp.Tests.Core.OutboxTestHarness;

namespace CQRSharp.Tests.Core;

/// <summary>
///     What a handler publishes while the outbox processor delivers a message to it is owned by that delivery, exactly as
///     what a request's handler publishes is owned by the request: stored once the delivery succeeded (with the delivery
///     transaction's commit when it runs in one), discarded when it fails, so a failed attempt and its retry never both
///     publish. The handler under test relays each <see cref="RelaySource" /> as a <see cref="Relayed" />; the store keeps
///     the relayed messages aside (the processor never claims them) and writes "store" to the transaction log shared with
///     the unit of work and the inbox, so a test sees where the relay was stored relative to the commit and the record.
/// </summary>
public sealed class OutboxDeliveryPublishTests
{
    [Fact(DisplayName = "A delivery's publishes reach a store that does not join its transaction after the commit, and before the inbox record")]
    public async Task Publishes_are_stored_after_the_commit()
    {
        var fixture = Fixture.Create(storeJoins: false, inboxJoins: false);
        await using var _ = fixture.Provider;
        var log = await fixture.RunUntilDeliveredAsync(new RelaySource(1));

        log.Should().Equal("begin", "commit", "store", "record");
        fixture.Store.SetAside.Select(m => m.NotificationType).Should().Equal(Relayed.Name);
    }

    [Fact(DisplayName = "A delivery's publishes reach a store that joins its transaction inside it, before the commit")]
    public async Task Publishes_are_stored_inside_a_joined_transaction()
    {
        var fixture = Fixture.Create(storeJoins: true, inboxJoins: true);
        await using var _ = fixture.Provider;
        var log = await fixture.RunUntilDeliveredAsync(new RelaySource(1));

        log.Should().Equal("begin", "record", "store", "commit");
        fixture.Store.SetAside.Should().ContainSingle();
    }

    [Fact(DisplayName = "A delivery whose commit fails publishes nothing; its retry publishes once")]
    public async Task A_failed_commit_publishes_nothing()
    {
        var fixture = Fixture.Create(storeJoins: false, inboxJoins: false);
        await using var _ = fixture.Provider;
        fixture.UnitOfWork.FailNextCommit(new IOException("deadlock"));
        var log = await fixture.RunUntilDeliveredAsync(new RelaySource(1));

        log.Should().Equal("begin", "commit-failed", "rollback", "begin", "commit", "store", "record");
        fixture.Probe.Attempts.Should().Equal(1, 1);
        fixture.Store.SetAside.Should().ContainSingle("the attempt whose commit failed announced work that was rolled back");
    }

    [Fact(DisplayName = "Without a unit of work, a delivery that fails after publishing publishes nothing; its retry publishes once")]
    public async Task A_failed_attempt_publishes_nothing()
    {
        var fixture = Fixture.Create(storeJoins: false, inboxJoins: false, unitOfWork: false);
        await using var _ = fixture.Provider;
        fixture.Probe.FailAttempts = 1;
        var log = await fixture.RunUntilDeliveredAsync(new RelaySource(1));

        log.Should().Equal("store", "record");
        fixture.Probe.Attempts.Should().Equal(1, 1);
        fixture.Store.SetAside.Should().ContainSingle("the failed attempt's relay describes work that did not happen");
    }

    [Fact(DisplayName = "A delivery a joined inbox finds already recorded rolls back what it published with its work")]
    public async Task A_duplicate_delivery_publishes_nothing()
    {
        var fixture = Fixture.Create(storeJoins: false, inboxJoins: true, reportDuplicate: true);
        await using var _ = fixture.Provider;
        var log = await fixture.RunUntilDeliveredAsync(new RelaySource(1));

        log.Should().Equal("begin", "record-duplicate", "rollback");
        fixture.Store.SetAside.Should().BeEmpty("the duplicate delivery's work was undone");
    }

    [Fact(DisplayName = "What a request the handler sends publishes is settled with the delivery's commit, not at the request's end")]
    public async Task A_nested_request_settles_with_the_delivery()
    {
        var fixture = Fixture.Create(storeJoins: false, inboxJoins: false);
        await using var _ = fixture.Provider;
        var log = await fixture.RunUntilDeliveredAsync(new RelaySource(1, ThroughCommand: true));

        log.Should().Equal("begin", "commit", "store", "record");
        fixture.Store.SetAside.Should().ContainSingle();
    }

    private sealed record Fixture(ServiceProvider Provider, FakeTimeProvider Time, RelayProbe Probe, RecordingOutboxStore Store, RecordingUnitOfWork UnitOfWork, TransactionLog Log)
    {
        public static Fixture Create(bool storeJoins, bool inboxJoins, bool unitOfWork = true, bool reportDuplicate = false)
        {
            var log = new TransactionLog();
            var probe = new RelayProbe();
            var recordingUnitOfWork = new RecordingUnitOfWork(log);
            RecordingOutboxStore? store = null;
            var (provider, time, _) = BuildProbed(
                p => p.Retry.BaseDelay = TimeSpan.FromSeconds(1),
                inbox: new RecordingInboxStore(inboxJoins, log) { ReportDuplicateOnNextRecord = reportDuplicate },
                configureBuilder: b =>
                {
                    b.Services.AddSingleton(probe);
                    if (unitOfWork) b.UseUnitOfWork(_ => recordingUnitOfWork);
                },
                outbox: clock => store = new RecordingOutboxStore(storeJoins, log, clock, m => m.NotificationType == Relayed.Name));
            return new Fixture(provider, time, probe, store!, recordingUnitOfWork, log);
        }

        // Stores the source outside any request, then runs the processor until the source's message is settled: delivered,
        // after as many retries as the test makes it fail, each run when it comes due. Returns what the delivery logged,
        // without the store of the source itself.
        public async Task<IReadOnlyList<string>> RunUntilDeliveredAsync(RelaySource source)
        {
            await PublishAsync(Provider, source);
            var published = Log.Entries.Count;
            var processor = Processor(Provider);
            await processor.StartAsync(CancellationToken.None);
            try
            {
                await SettleAsync(Provider, Time, () => Store.Held);
                Store.Held.Should().BeEmpty("the source was delivered and marked processed");
            }
            finally
            {
                await processor.StopAsync(CancellationToken.None);
            }

            return Log.Entries.Skip(published).ToArray();
        }
    }
}

/// <summary>Counts the deliveries of each source and fails the first <see cref="FailAttempts" /> after they published.</summary>
public sealed class RelayProbe
{
    private int _attempts;

    public ConcurrentQueue<int> Attempts { get; } = new();

    public int FailAttempts { get; set; }

    /// <summary>Records an attempt; true when it is one of the attempts that fail.</summary>
    public bool Attempt(int seq)
    {
        Attempts.Enqueue(seq);
        return Interlocked.Increment(ref _attempts) <= FailAttempts;
    }
}

[NotificationName("tests.relay.source")]
public sealed record RelaySource(int Seq, bool ThroughCommand = false) : INotification;

[NotificationName(Name)]
public sealed record Relayed(int Seq) : INotification
{
    public const string Name = "tests.relay.relayed";
}

public sealed class RelaySourceHandler(ICqrsDispatcher dispatcher, RelayProbe probe) : INotificationHandler<RelaySource>
{
    public async Task Handle(RelaySource notification, CancellationToken cancellationToken)
    {
        var fails = probe.Attempt(notification.Seq);
        if (notification.ThroughCommand)
            (await dispatcher.Send(new RelayCommand(notification.Seq), cancellationToken)).IsSuccess.Should().BeTrue();
        else
            await dispatcher.Publish(new Relayed(notification.Seq), cancellationToken);

        if (fails) throw new InvalidOperationException("relay failed after publishing");
    }
}

// Subscribed, so a relay is stored as a message at all: the outbox stores one message per subscribed handler.
public sealed class RelayedHandler : INotificationHandler<Relayed>
{
    public Task Handle(Relayed notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class RelayCommand(int seq) : CommandBase
{
    public int Seq { get; } = seq;
}

public sealed class RelayCommandHandler(ICqrsDispatcher dispatcher) : ICommandHandler<RelayCommand>
{
    public async Task<CommandResult> Handle(RelayCommand command, CancellationToken cancellationToken)
    {
        await dispatcher.Publish(new Relayed(command.Seq), cancellationToken);
        return CommandResult.FromSuccess();
    }
}
