using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Transports;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using CQRSharp.Transports;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using static CQRSharp.Tests.Core.OutboxTestHarness;

namespace CQRSharp.Tests.Core;

/// <summary>
///     <see cref="INotificationIntake" />: a received notification is deduplicated by its message id through the inbox, read
///     back through the application's serializer, and stored once per local handler (never for a transport); what cannot
///     be stored is reported so the transport can acknowledge, hold or reject it. When the store and the inbox both join the
///     unit of work's transaction the messages and the record commit together; otherwise the messages are stored first and
///     recorded after.
/// </summary>
public sealed class NotificationIntakeTests
{
    private const string Shared = "tests.transport.shared";
    private const string Source = "broker:billing";

    private static (ServiceProvider Provider, FakeTimeProvider Time, CapturingLoggerProvider Logs) Build(
        Action<OutboxProcessorOptions>? configure = null,
        IInboxStore? inbox = null,
        Func<TimeProvider, IOutboxStore>? outbox = null,
        Action<ICqrsBuilder>? configureBuilder = null)
    {
        var logs = new CapturingLoggerProvider();
        var (provider, time, _) = BuildProbed(configure, inbox, configureBuilder, outbox, services =>
        {
            services.AddSingleton<SharedNotificationLog>();
            services.AddSingleton<ILoggerProvider>(logs);
            // A transport that forwards the shared notification: what comes in must still never go back out through it.
            services.AddSingleton<INotificationTransport>(new ScriptedTransport(ScriptedTransport.DefaultName, Shared, "tests.transport.forwarded"));
        });
        return (provider, time, logs);
    }

    private static byte[] Payload(IServiceProvider provider, INotification notification)
        => provider.GetRequiredService<INotificationSerializer>().Serialize(notification);

    private static InboundNotification Inbound(IServiceProvider provider, string? messageId, INotification? notification = null, string source = Source)
        => new("broker", source, messageId, Shared, Payload(provider, notification ?? new SharedNotification(1)), null, null, null);

    private static async Task<IntakeResult> AcceptAsync(IServiceProvider provider, InboundNotification message)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INotificationIntake>().AcceptAsync(message, CancellationToken.None);
    }

    [Fact(DisplayName = "Intake: a received notification is stored once per local handler, never for a transport, and recorded in the inbox")]
    public async Task A_notification_is_stored_for_the_local_handlers()
    {
        var (provider, _, _) = Build();
        await using var _ = provider;
        using var received = new InstrumentRecorder<long>(provider, CqrsTelemetry.Instruments.TransportReceived);
        var messageId = Guid.NewGuid();

        var result = await AcceptAsync(provider, Inbound(provider, messageId.ToString("N"), new SharedNotification(9)));

        result.Should().Be(new IntakeResult(IntakeOutcome.Stored, 1, null, null));
        var stored = Stored(provider).Should().ContainSingle().Subject;
        stored.HandlerName.Should().NotBe(ScriptedTransport.DefaultName, "what came in through a transport is not sent back out");
        stored.NotificationType.Should().Be(Shared);
        provider.GetRequiredService<INotificationSerializer>().Deserialize(Shared, stored.Payload).Should().Be(new SharedNotification(9));
        (await provider.GetRequiredService<IInboxStore>().IsDeliveredAsync(messageId, Source, CancellationToken.None)).Should().BeTrue();

        var measurement = received.Measurements.Should().ContainSingle().Subject;
        measurement.Tag(CqrsTelemetry.Tags.Outcome).Should().Be("stored");
        measurement.Tag(CqrsTelemetry.Tags.Transport).Should().Be("broker");
        measurement.Tag(CqrsTelemetry.Tags.NotificationName).Should().Be(Shared);
    }

    [Fact(DisplayName = "Intake: a redelivery is recognised by its message id within its source, and stores nothing again")]
    public async Task A_redelivery_is_a_duplicate()
    {
        var (provider, _, _) = Build();
        await using var _ = provider;

        (await AcceptAsync(provider, Inbound(provider, "order-42"))).Outcome.Should().Be(IntakeOutcome.Stored);
        (await AcceptAsync(provider, Inbound(provider, "order-42"))).Outcome.Should().Be(IntakeOutcome.Duplicate);
        (await AcceptAsync(provider, Inbound(provider, "order-42", source: "broker:shipping"))).Outcome
            .Should().Be(IntakeOutcome.Stored, "another source is another namespace of message ids");

        Stored(provider).Should().HaveCount(2);
        (await provider.GetRequiredService<IInboxStore>().IsDeliveredAsync(MessageIdGuid.From("order-42"), Source, CancellationToken.None))
            .Should().BeTrue("a message id that is not a Guid is recorded under its name-based UUID");
    }

    [Fact(DisplayName = "Intake: without a message id, or with the inbox turned off, nothing is deduplicated")]
    public async Task Without_an_id_or_the_inbox_nothing_is_deduplicated()
    {
        var (provider, _, _) = Build();
        await using var _ = provider;
        (await AcceptAsync(provider, Inbound(provider, null))).Outcome.Should().Be(IntakeOutcome.Stored);
        (await AcceptAsync(provider, Inbound(provider, null))).Outcome.Should().Be(IntakeOutcome.Stored);
        Stored(provider).Should().HaveCount(2);

        var (withoutInbox, _, _) = Build(p => p.UseInbox = false);
        await using var __ = withoutInbox;
        (await AcceptAsync(withoutInbox, Inbound(withoutInbox, "m-1"))).Outcome.Should().Be(IntakeOutcome.Stored);
        (await AcceptAsync(withoutInbox, Inbound(withoutInbox, "m-1"))).Outcome.Should().Be(IntakeOutcome.Stored);
        Stored(withoutInbox).Should().HaveCount(2);
    }

    [Fact(DisplayName = "Intake: a notification no local handler receives stores nothing and records nothing")]
    public async Task A_notification_without_a_local_handler_is_not_stored()
    {
        var (provider, _, _) = Build();
        await using var _ = provider;
        var message = new InboundNotification("broker", Source, "m-1", "tests.transport.forwarded",
            Payload(provider, new ForwardedNotification(1, null)), null, null, null);

        var result = await AcceptAsync(provider, message);

        result.Outcome.Should().Be(IntakeOutcome.NoSubscribers);
        Stored(provider).Should().BeEmpty("not even for the transport that forwards the type");
        (await provider.GetRequiredService<IInboxStore>().IsDeliveredAsync(MessageIdGuid.From("m-1"), Source, CancellationToken.None))
            .Should().BeFalse("a handler added later must not be denied the redeliveries");
    }

    [Fact(DisplayName = "Intake: an unknown notification is held while it is young enough for an instance that knows it, then given up")]
    public async Task An_unknown_notification_is_held_then_given_up()
    {
        var (provider, time, _) = Build(p =>
        {
            p.PollingInterval = TimeSpan.FromSeconds(5);
            p.UnknownRecipientGracePeriod = TimeSpan.FromHours(1);
        });
        await using var _ = provider;
        using var received = new InstrumentRecorder<long>(provider, CqrsTelemetry.Instruments.TransportReceived);
        var now = time.GetUtcNow().UtcDateTime;

        var young = await AcceptAsync(provider, new InboundNotification("broker", Source, "m-1", "tests.transport.added-later", [1], now, null, null));
        var old = await AcceptAsync(provider, new InboundNotification("broker", Source, "m-2", "tests.transport.added-later", [1], now.AddHours(-2), null, null));

        young.Outcome.Should().Be(IntakeOutcome.UnknownNotification);
        young.RetryAfter.Should().Be(TimeSpan.FromSeconds(5), "a message just sent waits one polling interval, as the processor defers it");
        old.Outcome.Should().Be(IntakeOutcome.UnknownNotification);
        old.RetryAfter.Should().BeNull("past the grace period no instance is going to know it");
        old.Detail.Should().Contain("grace period");
        received.Measurements.Select(m => m.Tag(CqrsTelemetry.Tags.Outcome)).Should().Equal("unknown_held", "unknown");
        Stored(provider).Should().BeEmpty();
    }

    [Fact(DisplayName = "Intake: a payload the serializer cannot read, or a message without a name, is unreadable")]
    public async Task An_unreadable_message_is_reported()
    {
        var (provider, _, _) = Build();
        await using var _ = provider;
        using var received = new InstrumentRecorder<long>(provider, CqrsTelemetry.Instruments.TransportReceived);

        var garbage = await AcceptAsync(provider, new InboundNotification("broker", Source, "m-1", Shared, "{not json"u8.ToArray(), null, null, null));
        var nameless = await AcceptAsync(provider, new InboundNotification("broker", Source, "m-2", "", [1], null, null, null));

        garbage.Outcome.Should().Be(IntakeOutcome.UnreadablePayload);
        nameless.Outcome.Should().Be(IntakeOutcome.UnreadablePayload);
        nameless.Detail.Should().Contain("no notification name");
        received.Measurements.Should().HaveCount(2).And.OnlyContain(m => m.Tag(CqrsTelemetry.Tags.Outcome) == "unreadable");
        Stored(provider).Should().BeEmpty();
    }

    [Fact(DisplayName = "Intake: without a joining transaction the messages are stored first and recorded after; a failed record keeps them")]
    public async Task Stored_first_then_recorded()
    {
        var log = new TransactionLog();
        var inbox = new RecordingInboxStore(joinsUnitOfWork: false, log);
        RecordingOutboxStore? store = null;
        var (provider, _, logs) = Build(inbox: inbox, outbox: clock => store = new RecordingOutboxStore(false, log, clock));
        await using var _ = provider;

        (await AcceptAsync(provider, Inbound(provider, "m-1"))).Outcome.Should().Be(IntakeOutcome.Stored);
        log.Entries.Should().Equal("store", "record");

        inbox.RecordFailure = new InvalidOperationException("inbox down");
        (await AcceptAsync(provider, Inbound(provider, "m-2"))).Outcome.Should().Be(IntakeOutcome.Stored, "the messages stand");
        store!.Held.Should().HaveCount(2);
        logs.WithId(5103).Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    [Fact(DisplayName = "Intake: when the store and the inbox join the unit of work, the messages and the record commit in one transaction")]
    public async Task Joining_stores_commit_together()
    {
        var log = new TransactionLog();
        var unitOfWork = new RecordingUnitOfWork(log);
        var inbox = new RecordingInboxStore(joinsUnitOfWork: true, log);
        var (provider, _, _) = Build(
            inbox: inbox,
            outbox: clock => new RecordingOutboxStore(true, log, clock),
            configureBuilder: b => b.UseUnitOfWork(_ => unitOfWork));
        await using var _ = provider;

        (await AcceptAsync(provider, Inbound(provider, "m-1"))).Should().Be(new IntakeResult(IntakeOutcome.Stored, 1, null, null));
        log.Entries.Should().Equal("begin", "store", "record", "commit");

        // A concurrent intake of the same message recorded first: this one's messages go back with its transaction.
        inbox.ReportDuplicateOnNextRecord = true;
        (await AcceptAsync(provider, Inbound(provider, "m-2"))).Outcome.Should().Be(IntakeOutcome.Duplicate);
        log.Entries.Skip(4).Should().Equal("begin", "store", "record-duplicate", "rollback");
    }

    [Fact(DisplayName = "Intake: with a unit of work whose transaction the store does not join, the intake is stored first and recorded after")]
    public async Task A_store_that_does_not_join_is_not_atomic()
    {
        var log = new TransactionLog();
        var unitOfWork = new RecordingUnitOfWork(log);
        var (provider, _, _) = Build(
            inbox: new RecordingInboxStore(joinsUnitOfWork: true, log),
            outbox: clock => new RecordingOutboxStore(false, log, clock),
            configureBuilder: b => b.UseUnitOfWork(_ => unitOfWork));
        await using var _ = provider;

        (await AcceptAsync(provider, Inbound(provider, "m-1"))).Outcome.Should().Be(IntakeOutcome.Stored);

        log.Entries.Should().Equal("begin", "rollback", "store", "record");
    }

    [Fact(DisplayName = "Intake: a store that fails fails the intake, records nothing, and is measured as failed")]
    public async Task A_failing_store_fails_the_intake()
    {
        var log = new TransactionLog();
        var inbox = new RecordingInboxStore(joinsUnitOfWork: false, log);
        var (provider, _, _) = Build(
            inbox: inbox,
            outbox: clock => new RecordingOutboxStore(false, log, clock) { StoreFailure = new InvalidOperationException("store down") });
        await using var _ = provider;
        using var received = new InstrumentRecorder<long>(provider, CqrsTelemetry.Instruments.TransportReceived);

        var act = () => AcceptAsync(provider, Inbound(provider, "m-1"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("store down");
        log.Entries.Should().Equal("store-failed");
        received.Measurements.Should().ContainSingle().Which.Tag(CqrsTelemetry.Tags.Outcome).Should().Be("failed");
    }

    [Theory(DisplayName = "Intake: a message without a transport or source name, or with a source name too long for the inbox, is refused")]
    [InlineData("", Source)]
    [InlineData("broker", "")]
    [InlineData("broker", null)]
    public async Task An_invalid_source_is_refused(string transport, string? source)
    {
        var (provider, _, _) = Build();
        await using var _ = provider;
        var message = Inbound(provider, "m-1") with { Transport = transport, Source = source ?? new string('q', 257) };

        var act = () => AcceptAsync(provider, message);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
