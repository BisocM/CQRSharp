using CQRSharp.Core.Outbox;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static CQRSharp.Tests.Core.OutboxTestHarness;

namespace CQRSharp.Tests.Core;

/// <summary>
///     The processor's delivery mechanics: bounded parallelism that keeps partitions in order, draining whatever is due
///     without waiting, the in-process wake-up that skips the polling interval, the inbox that turns a redelivery into a
///     no-op, and the configurable back-off. Runs the real processor over the in-memory store and generated
///     subscriptions, on a <see cref="FakeTimeProvider" />: polling intervals and back-offs pass only when a test moves
///     the clock, and each test waits for the processor to drain (<see cref="OutboxTestHarness.DrainAsync" />) before
///     it looks at the outcome.
/// </summary>
public sealed class OutboxProcessorDeliveryTests
{
    [Fact(DisplayName = "A message stored by this process is delivered without waiting for the polling interval")]
    public async Task Storing_a_message_wakes_the_processor()
    {
        var (provider, _, probe) = BuildProbed(p => p.PollingInterval = TimeSpan.FromHours(1));
        await using var _ = provider;
        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            // The first poll found nothing: from here the processor waits an hour on a clock that never moves, so only
            // the wake-up signal of the store below can make it claim again.
            await EmptyClaimsAsync(provider, 1);
            await PublishAsync(provider, new ParallelProbe(1, "a"));

            await EmptyClaimsAsync(provider, 2);
            probe.Completed.Should().ContainSingle("the message was delivered without the clock moving");
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "A backlog larger than a batch is claimed batch after batch, without waiting for the polling interval in between")]
    public async Task A_backlog_is_drained_without_waiting()
    {
        var (provider, _, probe) = BuildProbed(p =>
        {
            p.PollingInterval = TimeSpan.FromHours(1);
            p.BatchSize = 2;
        });
        await using var _ = provider;
        await PublishAsync(provider, Enumerable.Range(1, 7).Select(seq => (INotification)new ParallelProbe(seq, null)).ToArray());
        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await DrainAsync(provider);
            probe.Completed.Should().HaveCount(7, "all four batches were delivered without the clock moving");
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "The messages of one partition are delivered one after another, without waiting for the polling interval in between")]
    public async Task A_partition_is_drained_without_waiting()
    {
        var (provider, _, probe) = BuildProbed(p => p.PollingInterval = TimeSpan.FromHours(1));
        await using var _ = provider;
        // A partition hands out only its head: each of these becomes claimable once the one before it is delivered.
        await PublishAsync(provider, Enumerable.Range(1, 4).Select(seq => (INotification)new ParallelProbe(seq, "a")).ToArray());
        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await DrainAsync(provider);
            probe.Completed.Should().HaveCount(4, "the whole partition was delivered without the clock moving");
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        probe.Completed.Select(p => p.Seq).Should().Equal(1, 2, 3, 4);
    }

    [Fact(DisplayName = "With parallelism, a batch is delivered concurrently while each partition still goes one at a time")]
    public async Task Parallel_delivery_respects_partitions()
    {
        var (provider, _, probe) = BuildProbed(p => p.MaxDegreeOfParallelism = 4);
        await using var _ = provider;
        probe.HoldEachDelivery = true;

        // Stored before the processor starts: each store wakes the processor, and a poll that raced the publishing
        // would claim a partial batch and then wait on its held deliveries.
        await PublishAsync(provider,
            new ParallelProbe(1, "a"), new ParallelProbe(2, "a"),
            new ParallelProbe(3, "b"), new ParallelProbe(4, "b"),
            new ParallelProbe(5, null), new ParallelProbe(6, null));

        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            // The first batch: the two partition heads plus the two unpartitioned messages, all in flight together.
            await probe.EnteredAsync(4);
            probe.Entered.Select(p => p.Seq).Should().BeEquivalentTo([1, 3, 5, 6]);
            probe.MaxConcurrency.Should().Be(4);

            // Let the first batch finish and the partition tails flow: the next poll claims 2 and 4.
            probe.HoldEachDelivery = false;
            probe.ReleaseAll();
            await DrainAsync(provider);
            probe.Completed.Should().HaveCount(6, "every message was delivered");
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        var a = probe.Completed.Where(p => p.Key == "a").Select(p => p.Seq).ToList();
        var b = probe.Completed.Where(p => p.Key == "b").Select(p => p.Seq).ToList();
        a.Should().Equal(1, 2);
        b.Should().Equal(3, 4);
        probe.MaxConcurrency.Should().BeLessThanOrEqualTo(4);
    }

    [Fact(DisplayName = "A message already recorded in the inbox is skipped and marked processed as a duplicate")]
    public async Task A_recorded_delivery_is_not_run_again()
    {
        var (provider, _, probe) = BuildProbed();
        await using var _ = provider;
        await PublishAsync(provider, new ParallelProbe(1, null));
        var stored = Stored(provider).Single();
        var inbox = provider.GetRequiredService<IInboxStore>();
        (await inbox.RecordDeliveryAsync(stored.Id, stored.HandlerName, CancellationToken.None)).Should().BeTrue();

        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await DrainAsync(provider);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        Stored(provider).Should().BeEmpty("the message was marked processed as a duplicate");
        probe.Completed.Should().BeEmpty("a delivery the inbox already knows must not run the handler again");
    }

    [Fact(DisplayName = "Every completed delivery is recorded in the inbox")]
    public async Task Completed_deliveries_are_recorded()
    {
        var (provider, _, probe) = BuildProbed();
        await using var _ = provider;
        await PublishAsync(provider, new ParallelProbe(1, null));
        var stored = Stored(provider).Single();
        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await DrainAsync(provider);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        probe.Completed.Should().HaveCount(1);
        Stored(provider).Should().BeEmpty("the message was marked processed");
        (await provider.GetRequiredService<IInboxStore>().IsDeliveredAsync(stored.Id, stored.HandlerName, CancellationToken.None))
            .Should().BeTrue();
    }

    [Fact(DisplayName = "With the inbox disabled, deliveries are neither checked nor recorded")]
    public async Task The_inbox_can_be_switched_off()
    {
        var (provider, _, probe) = BuildProbed(p => p.UseInbox = false);
        await using var _ = provider;
        await PublishAsync(provider, new ParallelProbe(1, null));
        var stored = Stored(provider).Single();
        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await DrainAsync(provider);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        Stored(provider).Should().BeEmpty("the message was marked processed");
        probe.Completed.Should().HaveCount(1);
        (await provider.GetRequiredService<IInboxStore>().IsDeliveredAsync(stored.Id, stored.HandlerName, CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact(DisplayName = "An inbox check that fails charges no attempt, even the last one, and runs no handler: the message waits out its lease")]
    public async Task Failed_inbox_check_charges_no_attempt()
    {
        var inbox = new RecordingInboxStore(joinsUnitOfWork: false, new TransactionLog()) { CheckFailure = new IOException("inbox unreachable") };
        var (provider, time, probe) = BuildProbed(p => p.MaxAttempts = 1, inbox: inbox);
        await using var _ = provider;
        await PublishAsync(provider, new ParallelProbe(1, null));
        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await DrainAsync(provider);
            probe.Attempts.Should().BeEmpty("the handler never ran");
            var waiting = Stored(provider).Should().ContainSingle().Subject;
            waiting.AttemptCount.Should().Be(0);
            waiting.Status.Should().Be(OutboxMessageStatus.InProgress, "the message stays under its lease, which is its back-off");

            inbox.CheckFailure = null;
            time.Advance(new InMemoryOutboxStoreOptions().VisibilityTimeout);
            await DrainAsync(provider);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        probe.Completed.Should().HaveCount(1);
        Stored(provider).Should().BeEmpty();
    }

    [Fact(DisplayName = "With a unit of work and an inbox that does not join it, a failed commit records nothing and the redelivery runs the handler again")]
    public async Task Failed_commit_with_a_non_joining_inbox_is_redelivered()
    {
        var log = new TransactionLog();
        var unitOfWork = new RecordingUnitOfWork(log);
        unitOfWork.FailNextCommit(new IOException("deadlock"));
        var inbox = new RecordingInboxStore(joinsUnitOfWork: false, log);
        var (provider, time, probe) = BuildProbed(
            inbox: inbox,
            configureBuilder: b => b.UseUnitOfWork(_ => unitOfWork));
        await using var _ = provider;
        await PublishAsync(provider, new ParallelProbe(1, null));
        var stored = Stored(provider).Single();
        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await DrainAsync(provider);
            var failed = Stored(provider).Should().ContainSingle().Subject;
            failed.AttemptCount.Should().Be(1, "the failed commit is a failed attempt");
            (await inbox.IsDeliveredAsync(stored.Id, stored.HandlerName, CancellationToken.None))
                .Should().BeFalse("a delivery whose commit failed must not be marked done");

            time.Advance(failed.NextRetryAt!.Value - time.GetUtcNow().UtcDateTime);
            await DrainAsync(provider);
            Stored(provider).Should().BeEmpty("the redelivery was processed");
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        probe.Completed.Should().HaveCount(2, "the redelivery ran the handler again instead of skipping it as a duplicate");
        log.Entries.Should().Equal("begin", "commit-failed", "rollback", "begin", "commit", "record");
    }

    [Fact(DisplayName = "An inbox that joins the delivery's transaction records inside it, before the commit")]
    public async Task Joining_inbox_records_before_the_commit()
    {
        var log = new TransactionLog();
        var unitOfWork = new RecordingUnitOfWork(log);
        var inbox = new RecordingInboxStore(joinsUnitOfWork: true, log);
        var (provider, _, probe) = BuildProbed(
            inbox: inbox,
            configureBuilder: b => b.UseUnitOfWork(_ => unitOfWork));
        await using var _ = provider;
        await PublishAsync(provider, new ParallelProbe(1, null));
        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await DrainAsync(provider);
            Stored(provider).Should().BeEmpty("the message was processed");
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        probe.Completed.Should().HaveCount(1);
        log.Entries.Should().Equal("begin", "record", "commit");
    }

    [Fact(DisplayName = "A joining inbox that finds the delivery already recorded rolls the handler's work back")]
    public async Task Joining_inbox_duplicate_rolls_back()
    {
        var log = new TransactionLog();
        var unitOfWork = new RecordingUnitOfWork(log);
        var inbox = new RecordingInboxStore(joinsUnitOfWork: true, log) { ReportDuplicateOnNextRecord = true };
        var (provider, _, _) = BuildProbed(
            inbox: inbox,
            configureBuilder: b => b.UseUnitOfWork(_ => unitOfWork));
        await using var _ = provider;
        await PublishAsync(provider, new ParallelProbe(1, null));
        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await DrainAsync(provider);
            Stored(provider).Should().BeEmpty("the message was marked processed as a duplicate");
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        log.Entries.Should().Equal("begin", "record-duplicate", "rollback");
        unitOfWork.RollbackTokens.Should().Equal(CancellationToken.None);
    }

    [Fact(DisplayName = "A record that fails after the commit is logged; the committed delivery is not retried")]
    public async Task Record_failure_after_the_commit_is_not_a_failed_attempt()
    {
        var log = new TransactionLog();
        var unitOfWork = new RecordingUnitOfWork(log);
        var inbox = new RecordingInboxStore(joinsUnitOfWork: false, log) { RecordFailure = new IOException("inbox unreachable") };
        var (provider, _, probe) = BuildProbed(
            inbox: inbox,
            configureBuilder: b => b.UseUnitOfWork(_ => unitOfWork));
        await using var _ = provider;
        await PublishAsync(provider, new ParallelProbe(1, null));
        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await DrainAsync(provider);
            Stored(provider).Should().BeEmpty("the message was marked processed");
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        probe.Completed.Should().HaveCount(1, "the committed work is not run again");
        log.Entries.Should().Equal("begin", "commit", "record-failed");
    }

    [Fact(DisplayName = "Without a unit of work, a record that fails after the handler is logged; the delivered message is not retried")]
    public async Task Record_failure_without_a_unit_of_work_is_not_a_failed_attempt()
    {
        var log = new TransactionLog();
        var inbox = new RecordingInboxStore(joinsUnitOfWork: false, log) { RecordFailure = new TimeoutException("inbox timed out") };
        var (provider, _, probe) = BuildProbed(inbox: inbox);
        await using var _ = provider;
        await PublishAsync(provider, new ParallelProbe(1, null));
        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            // The clock stays still: a delivery counted as a failed attempt would wait out its back-off in the store.
            await DrainAsync(provider);
            Stored(provider).Should().BeEmpty("the delivered message was marked processed");
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        probe.Completed.Should().HaveCount(1, "the handler's work stands, so it is not run again");
        log.Entries.Should().Equal("record-failed");
    }

    [Fact(DisplayName = "Without a unit of work, a completed delivery is recorded and marked processed even though the host is stopping")]
    public async Task A_stopping_host_does_not_cancel_the_record_of_a_completed_delivery()
    {
        var log = new TransactionLog();
        var recording = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inbox = new RecordingInboxStore(joinsUnitOfWork: false, log)
        {
            BeforeRecord = token =>
            {
                recording.TrySetResult(token);
                return release.Task;
            }
        };
        var (provider, _, probe) = BuildProbed(inbox: inbox);
        await using var _ = provider;
        await PublishAsync(provider, new ParallelProbe(1, null));
        var stored = Stored(provider).Single();
        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);

        Task? stopping = null;
        try
        {
            // The handler has returned and the record is under way when the host begins to stop.
            var recordToken = await recording.Task.WaitAsync(TestContext.Current.CancellationToken);
            stopping = processor.StopAsync(CancellationToken.None);
            recordToken.IsCancellationRequested.Should().BeFalse("the handler's work already stands; a shutdown must not cost its record");
        }
        finally
        {
            release.TrySetResult();
            await (stopping ?? processor.StopAsync(CancellationToken.None));
        }

        probe.Completed.Should().HaveCount(1);
        log.Entries.Should().Equal("record");
        (await inbox.IsDeliveredAsync(stored.Id, stored.HandlerName, CancellationToken.None)).Should().BeTrue();
        Stored(provider).Should().BeEmpty("the processed mark is not cancelled by the shutdown either");
    }

    [Fact(DisplayName = "A failing delivery backs off by the configured schedule")]
    public async Task Retry_backoff_follows_the_configured_schedule()
    {
        var (provider, time, probe) = BuildProbed(p =>
        {
            p.Retry.BaseDelay = TimeSpan.FromSeconds(10);
            p.Retry.BackoffMultiplier = 3;
            p.Retry.MaxDelay = TimeSpan.FromSeconds(60);
            p.MaxAttempts = 5;
        });
        await using var _ = provider;
        probe.FailUntilAttempt = 3;
        await PublishAsync(provider, new ParallelProbe(1, null));
        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            // Each retry is made due by moving the clock to exactly its due time, and the clock stays there until the
            // processor has drained, so every back-off is measured from a known instant.
            await DrainAsync(provider);
            var first = Stored(provider).Should().ContainSingle().Subject;
            first.AttemptCount.Should().Be(1);
            var firstRetryAt = first.NextRetryAt!.Value;
            firstRetryAt.Should().Be(time.GetUtcNow().UtcDateTime.AddSeconds(10), "the first back-off is the base delay");

            time.Advance(firstRetryAt - time.GetUtcNow().UtcDateTime);
            await DrainAsync(provider);
            var second = Stored(provider).Should().ContainSingle().Subject;
            second.AttemptCount.Should().Be(2);
            var secondRetryAt = second.NextRetryAt!.Value;
            secondRetryAt.Should().Be(firstRetryAt.AddSeconds(30), "the second back-off is the base delay times the multiplier");

            time.Advance(secondRetryAt - time.GetUtcNow().UtcDateTime);
            await DrainAsync(provider);
            probe.Completed.Should().ContainSingle("the third attempt succeeded");
            Stored(provider).Should().BeEmpty();
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "The retry delay grows from BaseDelay, is capped at MaxDelay and is jittered symmetrically")]
    public void Retry_options_compute_bounded_delays()
    {
        var options = new OutboxRetryOptions { BaseDelay = TimeSpan.FromSeconds(2), BackoffMultiplier = 2, MaxDelay = TimeSpan.FromSeconds(20), JitterFactor = 0 };
        var random = new Random(1);

        OutboxProcessor.RetryDelay(options, 1, random).Should().Be(TimeSpan.FromSeconds(2));
        OutboxProcessor.RetryDelay(options, 3, random).Should().Be(TimeSpan.FromSeconds(8));
        OutboxProcessor.RetryDelay(options, 10, random).Should().Be(TimeSpan.FromSeconds(20), "the delay is capped");

        options.JitterFactor = 0.25;
        var delays = Enumerable.Range(0, 200).Select(_ => OutboxProcessor.RetryDelay(options, 2, random)).ToList();
        delays.Should().OnlyContain(d => d >= TimeSpan.FromSeconds(3) && d <= TimeSpan.FromSeconds(5));
        delays.Distinct().Count().Should().BeGreaterThan(10, "jitter spreads the delays");
    }

    [Fact(DisplayName = "An outbox enabled through Configure<OutboxOptions> after AddCqrsGenerated still has a processor that delivers")]
    public async Task Outbox_enabled_outside_the_builder_is_delivered()
    {
        var recorder = new DeliveryRecorder();
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                // A clock that never moves: only the stored message's wake-up signal can make the processor claim it.
                services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero)));
                services.AddSingleton(recorder);
                services.AddCqrsGenerated();
                services.Configure<OutboxOptions>(o => o.Mode = OutboxMode.Enabled);
                services.AddInMemoryOutboxStore();
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using (var scope = host.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish(new LateOutboxEvent(), TestContext.Current.CancellationToken);

            (await recorder.Delivered.Task.WaitAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact(DisplayName = "Outbox retry options reject an infinite multiplier, and the delay of valid options never overflows")]
    public void Retry_options_are_bounded()
    {
        var services = new ServiceCollection();
        services.AddOutboxProcessor();
        services.Configure<OutboxProcessorOptions>(o => o.Retry.BackoffMultiplier = double.PositiveInfinity);
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<OutboxProcessorOptions>>().Value;
        act.Should().Throw<OptionsValidationException>().WithMessage("*Multiplier*");

        var longest = new OutboxRetryOptions { BaseDelay = TimeSpan.FromSeconds(1), BackoffMultiplier = 10, MaxDelay = TimeSpan.FromDays(3650), JitterFactor = 0 };
        OutboxProcessor.RetryDelay(longest, int.MaxValue, new Random(1)).Should().Be(TimeSpan.FromDays(3650), "growth past double's range is capped");
        var immediate = new OutboxRetryOptions { BaseDelay = TimeSpan.Zero, BackoffMultiplier = 10, MaxDelay = TimeSpan.FromMinutes(1), JitterFactor = 0.2 };
        OutboxProcessor.RetryDelay(immediate, int.MaxValue, new Random(1)).Should().Be(TimeSpan.Zero, "a zero base delay never grows");
    }

    [Theory(DisplayName = "Outbox processor options reject an attempt budget below one and a grace period for unknown recipients that is not positive")]
    [InlineData(0, 60, "*MaxAttempts*")]
    [InlineData(3, 0, "*UnknownRecipientGracePeriod*")]
    [InlineData(3, -1, "*UnknownRecipientGracePeriod*")]
    public void Attempt_budget_and_grace_period_are_validated(int maxAttempts, int graceMinutes, string message)
    {
        var services = new ServiceCollection();
        services.AddOutboxProcessor();
        services.Configure<OutboxProcessorOptions>(o =>
        {
            o.MaxAttempts = maxAttempts;
            o.UnknownRecipientGracePeriod = TimeSpan.FromMinutes(graceMinutes);
        });
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<OutboxProcessorOptions>>().Value;
        act.Should().Throw<OptionsValidationException>().WithMessage(message);
    }
}

public sealed class DeliveryRecorder
{
    public TaskCompletionSource<bool> Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

[NotificationName("delivery.late-outbox")]
public sealed record LateOutboxEvent : INotification;

public sealed class LateOutboxEventHandler(DeliveryRecorder? recorder = null) : INotificationHandler<LateOutboxEvent>
{
    public Task Handle(LateOutboxEvent notification, CancellationToken cancellationToken)
    {
        recorder?.Delivered.TrySetResult(true);
        return Task.CompletedTask;
    }
}
