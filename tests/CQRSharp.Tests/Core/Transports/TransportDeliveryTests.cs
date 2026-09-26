using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Outbox;
using CQRSharp.Pipelines;
using CQRSharp.Persistence;
using CQRSharp.Tests.Shared;
using CQRSharp.Transports;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static CQRSharp.Tests.Core.OutboxTestHarness;

namespace CQRSharp.Tests.Core;

/// <summary>
///     The outbox processor and a notification transport: a transport's message is handed to it as stored, without being
///     read back and without the inbox, and what the transport answers decides the message's fate. Sent marks it
///     processed; Unavailable defers it without charging an attempt, so an outage never dead-letters anything; Rejected
///     charges an attempt and dead-letters at the last one, or at once when permanent; a send that throws is a rejection;
///     a send the shutdown interrupts is handed back.
/// </summary>
public sealed class TransportDeliveryTests
{
    private const string Forwarded = "tests.transport.forwarded";
    private const string Shared = "tests.transport.shared";

    private static (ServiceProvider Provider, Microsoft.Extensions.Time.Testing.FakeTimeProvider Time, CapturingLoggerProvider Logs) Build(
        ScriptedTransport transport, Action<OutboxProcessorOptions>? configure = null, IInboxStore? inbox = null)
    {
        var logs = new CapturingLoggerProvider();
        var (provider, time, _) = BuildProbed(configure, inbox, configureServices: services =>
        {
            services.AddSingleton<INotificationTransport>(transport);
            services.AddSingleton<SharedNotificationLog>();
            services.AddSingleton<ILoggerProvider>(logs);
        });
        return (provider, time, logs);
    }

    private static async Task RunAsync(IServiceProvider provider, Func<Task> body)
    {
        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await body();
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }
    }

    private static int Seq(IServiceProvider provider, OutboundNotification sent)
        => ((ForwardedNotification)provider.GetRequiredService<INotificationSerializer>().Deserialize(sent.NotificationName, sent.Payload)!).Seq;

    [Fact(DisplayName = "Transport delivery: a notification only the transport takes is sent once, as stored, and marked processed")]
    public async Task Sent_marks_the_message_processed()
    {
        var transport = new ScriptedTransport(ScriptedTransport.DefaultName, Forwarded);
        var (provider, _, _) = Build(transport);
        await using var _ = provider;
        using var outcomes = new InstrumentRecorder<long>(provider, CqrsTelemetry.Instruments.OutboxMessages);

        await PublishAsync(provider, new ForwardedNotification(1, "k"));
        var stored = Stored(provider).Should().ContainSingle().Subject;

        await RunAsync(provider, () => DrainAsync(provider));

        var sent = transport.Sends.Should().ContainSingle().Subject;
        sent.MessageId.Should().Be(stored.Id);
        sent.NotificationId.Should().Be(stored.NotificationId);
        sent.NotificationName.Should().Be(Forwarded);
        sent.Payload.Should().Equal(stored.Payload);
        sent.CreatedAt.Should().Be(stored.CreatedAt);
        sent.PartitionKey.Should().Be("k");
        sent.AttemptCount.Should().Be(0);
        Stored(provider).Should().BeEmpty("the processed message is gone from the store");
        outcomes.Measurements.Should().ContainSingle(m => m.Tag(CqrsTelemetry.Tags.Outcome) == "processed")
            .Which.Tag(CqrsTelemetry.Tags.NotificationHandler).Should().Be(ScriptedTransport.DefaultName);
    }

    [Fact(DisplayName = "Transport delivery: a notification with a local handler reaches both the handler and the transport")]
    public async Task Handler_and_transport_each_get_their_message()
    {
        var transport = new ScriptedTransport(ScriptedTransport.DefaultName, Shared);
        var (provider, _, _) = Build(transport);
        await using var _ = provider;

        await PublishAsync(provider, new SharedNotification(3));
        await RunAsync(provider, () => DrainAsync(provider));

        provider.GetRequiredService<SharedNotificationLog>().Received.Should().Equal(new SharedNotification(3));
        transport.Sends.Should().ContainSingle().Which.NotificationName.Should().Be(Shared);
        Stored(provider).Should().BeEmpty();
    }

    [Fact(DisplayName = "Transport delivery: Unavailable defers the message until RetryAfter without charging an attempt, then it is sent")]
    public async Task Unavailable_defers_without_an_attempt()
    {
        var transport = new ScriptedTransport(ScriptedTransport.DefaultName, Forwarded)
        {
            OnSend = (_, _) => Task.FromResult(TransportSendResult.Unavailable("the broker is down", TimeSpan.FromSeconds(30)))
        };
        var (provider, time, logs) = Build(transport, p => p.MaxAttempts = 1);
        await using var _ = provider;
        using var outcomes = new InstrumentRecorder<long>(provider, CqrsTelemetry.Instruments.OutboxMessages);

        await PublishAsync(provider, new ForwardedNotification(1, null));
        await RunAsync(provider, async () =>
        {
            await DrainAsync(provider);

            var deferred = Stored(provider).Should().ContainSingle().Subject;
            deferred.Status.Should().Be(OutboxMessageStatus.Pending, "even with a single attempt allowed, an outage dead-letters nothing");
            deferred.AttemptCount.Should().Be(0);
            deferred.NextRetryAt.Should().Be(time.GetUtcNow().UtcDateTime.AddSeconds(30));
            deferred.LastError.Should().Be("the broker is down");
            outcomes.Measurements.Should().ContainSingle().Which.Tag(CqrsTelemetry.Tags.Outcome).Should().Be("unavailable");
            logs.WithId(5029).Should().ContainSingle().Which.Level.Should().Be(LogLevel.Information);

            transport.OnSend = (_, _) => Task.FromResult(TransportSendResult.Sent);
            time.Advance(TimeSpan.FromSeconds(30));
            await DrainAsync(provider);
        });

        transport.Sends.Should().HaveCount(2);
        Stored(provider).Should().BeEmpty();
    }

    [Fact(DisplayName = "Transport delivery: Unavailable without RetryAfter backs off by the retry policy, still without an attempt")]
    public async Task Unavailable_without_a_delay_uses_the_back_off()
    {
        var transport = new ScriptedTransport(ScriptedTransport.DefaultName, Forwarded)
        {
            OnSend = (_, _) => Task.FromResult(TransportSendResult.Unavailable("not connected"))
        };
        var (provider, time, _) = Build(transport, p => p.Retry.BaseDelay = TimeSpan.FromSeconds(7));
        await using var _ = provider;

        await PublishAsync(provider, new ForwardedNotification(1, null));
        await RunAsync(provider, () => DrainAsync(provider));

        var deferred = Stored(provider).Should().ContainSingle().Subject;
        deferred.AttemptCount.Should().Be(0);
        deferred.NextRetryAt.Should().Be(time.GetUtcNow().UtcDateTime.AddSeconds(7));
    }

    [Fact(DisplayName = "Transport delivery: Rejected charges an attempt each time and dead-letters the message at the last one")]
    public async Task Rejected_is_retried_then_dead_lettered()
    {
        var transport = new ScriptedTransport(ScriptedTransport.DefaultName, Forwarded)
        {
            OnSend = (_, _) => Task.FromResult(TransportSendResult.Rejected("unroutable: no queue is bound"))
        };
        var (provider, time, logs) = Build(transport, p => p.MaxAttempts = 3);
        await using var _ = provider;

        await PublishAsync(provider, new ForwardedNotification(1, null));
        await RunAsync(provider, () => SettleAsync(provider, time));

        transport.Sends.Select(s => s.AttemptCount).Should().Equal(0, 1, 2);
        var deadLetter = (await provider.GetRequiredService<IOutboxStore>().GetDeadLettersAsync(10, CancellationToken.None))
            .Should().ContainSingle().Subject;
        deadLetter.HandlerName.Should().Be(ScriptedTransport.DefaultName);
        deadLetter.LastError.Should().Be("unroutable: no queue is bound");
        logs.WithId(5031).Should().HaveCount(3, "every rejected attempt is logged, as a failed handler's is").And.OnlyContain(e => e.Level == LogLevel.Warning);
        logs.WithId(5015).Should().ContainSingle("the last one dead-letters the message");
    }

    [Fact(DisplayName = "Transport delivery: a permanent rejection dead-letters the message at once")]
    public async Task A_permanent_rejection_dead_letters_at_once()
    {
        var transport = new ScriptedTransport(ScriptedTransport.DefaultName, Forwarded)
        {
            OnSend = (_, _) => Task.FromResult(TransportSendResult.Rejected("larger than the broker accepts", permanent: true))
        };
        var (provider, _, logs) = Build(transport, p => p.MaxAttempts = 5);
        await using var _ = provider;

        await PublishAsync(provider, new ForwardedNotification(1, null));
        await RunAsync(provider, () => DrainAsync(provider));

        transport.Sends.Should().ContainSingle();
        (await provider.GetRequiredService<IOutboxStore>().GetDeadLettersAsync(10, CancellationToken.None))
            .Should().ContainSingle().Which.LastError.Should().Be("larger than the broker accepts");
        logs.WithId(5030).Should().ContainSingle().Which.Level.Should().Be(LogLevel.Error);
    }

    [Fact(DisplayName = "Transport delivery: a send that throws is a rejection: an attempt is charged, with the exception as the error")]
    public async Task A_send_that_throws_charges_an_attempt()
    {
        var transport = new ScriptedTransport(ScriptedTransport.DefaultName, Forwarded)
        {
            OnSend = (_, _) => Task.FromException<TransportSendResult>(new InvalidOperationException("transport bug"))
        };
        var (provider, _, logs) = Build(transport);
        await using var _ = provider;

        await PublishAsync(provider, new ForwardedNotification(1, null));
        await RunAsync(provider, () => DrainAsync(provider));

        var failed = Stored(provider).Should().ContainSingle().Subject;
        failed.AttemptCount.Should().Be(1);
        failed.LastError.Should().Contain("transport bug");
        logs.WithId(5014).Should().ContainSingle().Which.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact(DisplayName = "Transport delivery: a result the transport never set counts as a rejection")]
    public async Task A_default_result_counts_as_a_rejection()
    {
        var transport = new ScriptedTransport(ScriptedTransport.DefaultName, Forwarded)
        {
            OnSend = (_, _) => Task.FromResult(default(TransportSendResult))
        };
        var (provider, _, _) = Build(transport);
        await using var _ = provider;

        await PublishAsync(provider, new ForwardedNotification(1, null));
        await RunAsync(provider, () => DrainAsync(provider));

        var failed = Stored(provider).Should().ContainSingle().Subject;
        failed.AttemptCount.Should().Be(1);
        failed.LastError.Should().Contain("returned no result");
    }

    [Fact(DisplayName = "Transport delivery: the stored payload is forwarded byte for byte, never read back, and the inbox is not used")]
    public async Task The_payload_is_forwarded_without_deserializing_or_the_inbox()
    {
        var transport = new ScriptedTransport(ScriptedTransport.DefaultName);
        // An inbox that fails every check: a delivery that asked it would never reach its recipient.
        var inboxLog = new TransactionLog();
        var inbox = new RecordingInboxStore(false, inboxLog) { CheckFailure = new InvalidOperationException("the inbox was asked") };
        var (provider, time, _) = Build(transport, inbox: inbox);
        await using var _ = provider;
        byte[] payload = [0xFF, 0x00, 0x7B, 0x01];
        var message = new OutboxMessage(
            Guid.NewGuid(), "tests.transport.written-by-another-version", ScriptedTransport.DefaultName, payload,
            time.GetUtcNow().UtcDateTime, OutboxMessageStatus.Pending, null, null);
        await provider.GetRequiredService<IOutboxStore>().StoreAsync([message], CancellationToken.None);

        await RunAsync(provider, () => DrainAsync(provider));

        var sent = transport.Sends.Should().ContainSingle().Subject;
        sent.Payload.Should().Equal(payload);
        sent.NotificationName.Should().Be("tests.transport.written-by-another-version");
        inboxLog.Entries.Should().BeEmpty("nothing is recorded for a transport's message");
        Stored(provider).Should().BeEmpty();
    }

    [Fact(DisplayName = "Transport delivery: a send the shutdown interrupts is handed back, with no attempt charged")]
    public async Task A_send_cancelled_by_shutdown_is_released()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new ScriptedTransport(ScriptedTransport.DefaultName, Forwarded)
        {
            OnSend = async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return TransportSendResult.Sent;
            }
        };
        var (provider, _, _) = Build(transport);
        await using var _ = provider;

        await PublishAsync(provider, new ForwardedNotification(1, null));
        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        provider.GetRequiredService<IOutboxSignal>().Signal();
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await processor.StopAsync(CancellationToken.None);

        var released = Stored(provider).Should().ContainSingle().Subject;
        released.Status.Should().Be(OutboxMessageStatus.Pending);
        released.AttemptCount.Should().Be(0);
        (await provider.GetRequiredService<IOutboxStore>().ClaimPendingAsync(10, CancellationToken.None))
            .Should().ContainSingle("a released message is claimable again at once, not after its lease");
    }

    [Fact(DisplayName = "Transport delivery: messages of one partition key are sent one at a time, in order, a deferred head holding the rest back")]
    public async Task A_partition_is_sent_in_order()
    {
        var first = true;
        var transport = new ScriptedTransport(ScriptedTransport.DefaultName, Forwarded)
        {
            OnSend = (_, _) =>
            {
                var result = first ? TransportSendResult.Unavailable("blocked", TimeSpan.FromSeconds(10)) : TransportSendResult.Sent;
                first = false;
                return Task.FromResult(result);
            }
        };
        var (provider, time, _) = Build(transport, p => p.MaxDegreeOfParallelism = 4);
        await using var _ = provider;

        await PublishAsync(provider, new ForwardedNotification(1, "order-7"), new ForwardedNotification(2, "order-7"), new ForwardedNotification(3, "order-7"));
        await RunAsync(provider, async () =>
        {
            await DrainAsync(provider);
            transport.Sends.Select(s => Seq(provider, s)).Should().Equal([1], "the rest of the partition waits behind its deferred head");

            await SettleAsync(provider, time);
        });

        transport.Sends.Select(s => Seq(provider, s)).Should().Equal(1, 1, 2, 3);
    }

    [Fact(DisplayName = "Transport delivery: a message for a transport this instance does not have is deferred for one that has it")]
    public async Task A_message_for_an_unknown_transport_is_deferred()
    {
        var transport = new ScriptedTransport(ScriptedTransport.DefaultName, Shared);
        var (provider, time, logs) = Build(transport);
        await using var _ = provider;
        var payload = provider.GetRequiredService<INotificationSerializer>().Serialize(new SharedNotification(1));
        await provider.GetRequiredService<IOutboxStore>().StoreAsync(
            [new OutboxMessage(Guid.NewGuid(), Shared, "another-transport", payload, time.GetUtcNow().UtcDateTime, OutboxMessageStatus.Pending, null, null)],
            CancellationToken.None);

        await RunAsync(provider, () => DrainAsync(provider));

        transport.Sends.Should().BeEmpty();
        Stored(provider).Should().ContainSingle().Which.NextRetryAt.Should().BeAfter(time.GetUtcNow().UtcDateTime);
        logs.WithId(5025).Should().ContainSingle();
    }
}
