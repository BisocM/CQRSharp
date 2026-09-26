using CQRSharp.Core.Outbox;
using CQRSharp.Persistence;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static CQRSharp.Tests.Core.OutboxTestHarness;

namespace CQRSharp.Tests.Core;

/// <summary>
///     <see cref="ICqrsDispatcher.PublishAt{TNotification}" /> and <see cref="ICqrsDispatcher.PublishAfter{TNotification}" />
///     end to end: the dispatcher stores a scheduled notification in the outbox with its due time as its place in the
///     order, the processor delivers it only once the fake clock reaches that time, and a scheduled publish that the
///     outbox could not honor fails before anything is buffered or stored.
/// </summary>
public sealed class ScheduledPublishTests
{
    private static readonly TimeSpan NeverPolls = TimeSpan.FromDays(7);

    [Fact(DisplayName = "PublishAt stores the notification with its due time and the processor delivers it only once it is due")]
    public async Task PublishAt_is_delivered_once_it_is_due()
    {
        var (provider, time, probe) = BuildProbed(p => p.PollingInterval = NeverPolls);
        await using var _ = provider;
        var dueAt = time.GetUtcNow().AddHours(1);

        await ScheduleAsync(provider, d => d.PublishAt(new ParallelProbe(1, null), dueAt));

        var stored = Stored(provider).Should().ContainSingle().Subject;
        stored.CreatedAt.Should().Be(dueAt.UtcDateTime, "its due time is its place in the delivery order");
        stored.NextRetryAt.Should().Be(dueAt.UtcDateTime, "it is not claimable before its due time");

        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await DrainAsync(provider);
            probe.Completed.Should().BeEmpty("the notification is not due yet");

            time.Advance(TimeSpan.FromMinutes(59));
            await DrainAsync(provider);
            probe.Completed.Should().BeEmpty("the notification is due a minute from now");

            time.Advance(TimeSpan.FromMinutes(1));
            await DrainAsync(provider);
            probe.Completed.Should().ContainSingle().Which.Seq.Should().Be(1);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "PublishAfter measures the delay on the application's clock")]
    public async Task PublishAfter_is_due_after_the_delay_on_the_application_clock()
    {
        var (provider, time, _) = BuildProbed();
        await using var _ = provider;
        var publishedAt = time.GetUtcNow().UtcDateTime;

        await ScheduleAsync(provider, d => d.PublishAfter(new ParallelProbe(1, null), TimeSpan.FromMinutes(90)));

        var stored = Stored(provider).Should().ContainSingle().Subject;
        stored.CreatedAt.Should().Be(publishedAt.AddMinutes(90));
        stored.NextRetryAt.Should().Be(publishedAt.AddMinutes(90));
    }

    [Fact(DisplayName = "A due time already past when the notification is stored makes it an ordinary durable publish, stamped with the time it is stored")]
    public async Task A_past_due_time_is_an_ordinary_publish()
    {
        var (provider, time, probe) = BuildProbed(p => p.PollingInterval = NeverPolls);
        await using var _ = provider;

        await ScheduleAsync(provider, d => d.PublishAt(new ParallelProbe(1, null), time.GetUtcNow().AddDays(-1)));
        await ScheduleAsync(provider, d => d.PublishAfter(new ParallelProbe(2, null), TimeSpan.Zero));

        Stored(provider).Should().HaveCount(2).And.OnlyContain(m => m.NextRetryAt == null && m.CreatedAt == time.GetUtcNow().UtcDateTime,
            "a message may not take a place in the order before messages that were already delivered");

        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await DrainAsync(provider);
            probe.Completed.Select(p => p.Seq).Should().Equal(1, 2);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "In its partition a scheduled notification is ordered by its due time: it holds back nothing published before it is due, and is delivered before anything published after")]
    public async Task A_scheduled_notification_takes_its_place_in_its_partition_at_its_due_time()
    {
        var (provider, time, probe) = BuildProbed(p => p.PollingInterval = NeverPolls);
        await using var _ = provider;
        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await ScheduleAsync(provider, d => d.PublishAfter(new ParallelProbe(1, "order-1"), TimeSpan.FromHours(1)));
            await PublishAsync(provider, new ParallelProbe(2, "order-1"));
            await DrainAsync(provider);
            probe.Completed.Select(p => p.Seq).Should().Equal([2], "the scheduled notification is not due, so it holds nothing back");

            time.Advance(TimeSpan.FromHours(1));
            await PublishAsync(provider, new ParallelProbe(3, "order-1"));
            await DrainAsync(provider);
            probe.Completed.Select(p => p.Seq).Should().Equal([2, 1, 3], "once due, it is ahead of what was published after its due time");
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }
    }

    [Theory(DisplayName = "A request's scheduled notification is stored when the request succeeds and discarded when it fails")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_scheduled_notification_settles_with_its_request(bool fail)
    {
        var (provider, time, _) = BuildProbed();
        await using var _ = provider;
        var dueAt = time.GetUtcNow().AddDays(2);

        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
                .Send(new ScheduleProbeCommand { Seq = 1, DueAt = dueAt, Fail = fail }, TestContext.Current.CancellationToken);
            result.IsSuccess.Should().Be(!fail);
        }

        if (fail)
            Stored(provider).Should().BeEmpty("the request that published it failed");
        else
            Stored(provider).Should().ContainSingle().Which.NextRetryAt.Should().Be(dueAt.UtcDateTime);
    }

    [Fact(DisplayName = "With the outbox off, a scheduled publish fails instead of being delivered at once")]
    public async Task Scheduling_without_the_outbox_fails()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddSingleton<DeliveryProbe>();
        services.AddCqrsGenerated();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        var at = () => dispatcher.PublishAt(new ParallelProbe(1, null), DateTimeOffset.UnixEpoch);
        var after = () => dispatcher.PublishAfter(new ParallelProbe(2, null), TimeSpan.FromMinutes(1));

        await at.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*'{typeof(ParallelProbe).FullName}'*outbox is off*UseOutbox*");
        await after.Should().ThrowAsync<InvalidOperationException>().WithMessage("*outbox is off*");
        provider.GetRequiredService<DeliveryProbe>().Entered.Should().BeEmpty("a scheduled publish never falls back to in-process delivery");
    }

    [Fact(DisplayName = "A scheduled publish of a notification the serializer does not name fails: it is not durable")]
    public async Task Scheduling_a_notification_that_is_not_durable_fails()
    {
        var (provider, _, _) = BuildProbed();
        await using var _ = provider;

        var unnamed = () => ScheduleAsync(provider, d => d.PublishAfter(new UnnamedScheduledNotification(), TimeSpan.FromMinutes(1)));
        var structure = () => ScheduleAsync(provider, d => d.PublishAfter(new ScheduledStructNotification(), TimeSpan.FromMinutes(1)));

        await unnamed.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*'{typeof(UnnamedScheduledNotification).FullName}'*gives it no name*[NotificationName]*");
        await structure.Should().ThrowAsync<InvalidOperationException>().WithMessage("*gives it no name*applies to classes*");
        Stored(provider).Should().BeEmpty();
    }

    [Fact(DisplayName = "A scheduled publish into a store that cannot schedule fails, naming the store, before a request buffers it")]
    public async Task Scheduling_into_a_store_that_cannot_schedule_fails()
    {
        var (provider, time, _) = BuildProbed(outbox: _ => new NullOutboxStore());
        await using var _ = provider;
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        var outsideRequest = () => dispatcher.PublishAt(new ParallelProbe(1, null), time.GetUtcNow().AddHours(1));
        var insideRequest = () => dispatcher.Send(new ScheduleProbeCommand { Seq = 2, DueAt = time.GetUtcNow().AddHours(1) });

        await outsideRequest.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*'{scope.ServiceProvider.GetRequiredService<IOutboxStore>().GetType().FullName}'*cannot hold a message until it is due*{nameof(ISchedulingOutboxStore)}*");
        await insideRequest.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cannot hold a message until it is due*");
        scope.ServiceProvider.GetRequiredService<ScopedOutbox>().Count.Should().Be(0, "nothing was buffered for the failed request");
    }

    [Fact(DisplayName = "A negative delay, or one that reaches past the calendar, is rejected at the call")]
    public async Task An_impossible_delay_is_rejected()
    {
        var (provider, _, _) = BuildProbed();
        await using var _ = provider;
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        var token = TestContext.Current.CancellationToken;
        ThrowsSynchronously<ArgumentOutOfRangeException>(() => dispatcher.PublishAfter(new ParallelProbe(1, null), TimeSpan.FromTicks(-1), token));
        ThrowsSynchronously<ArgumentOutOfRangeException>(() => dispatcher.PublishAfter(new ParallelProbe(1, null), TimeSpan.MaxValue, token));
        ThrowsSynchronously<ArgumentNullException>(() => dispatcher.PublishAt<ParallelProbe>(null!, DateTimeOffset.UnixEpoch, token));
        Stored(provider).Should().BeEmpty();
    }

    [Fact(DisplayName = "An ICqrsDispatcher written before scheduling inherits members that say so")]
    public void A_dispatcher_that_cannot_schedule_throws_NotSupportedException()
    {
        ICqrsDispatcher dispatcher = new PublishOnlyDispatcher();

        var token = TestContext.Current.CancellationToken;
        ThrowsSynchronously<NotSupportedException>(() => dispatcher.PublishAt(new ParallelProbe(1, null), DateTimeOffset.UnixEpoch, token))
            .Message.Should().Contain(typeof(PublishOnlyDispatcher).FullName!).And.Contain("PublishAt");
        ThrowsSynchronously<NotSupportedException>(() => dispatcher.PublishAfter(new ParallelProbe(1, null), TimeSpan.Zero, token));
    }

    // The call itself throws, before any task exists: an argument or implementation error, not a failed publish.
    private static TException ThrowsSynchronously<TException>(Func<Task> call) where TException : Exception
        => Assert.Throws<TException>(() => { _ = call(); });

    private static async Task ScheduleAsync(IServiceProvider provider, Func<ICqrsDispatcher, Task> schedule)
    {
        await using var scope = provider.CreateAsyncScope();
        await schedule(scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>());
    }

    private sealed record UnnamedScheduledNotification : INotification;

    private readonly record struct ScheduledStructNotification : INotification;

    // An implementation of the interface as it was before scheduling: it inherits the default members.
    private sealed class PublishOnlyDispatcher : ICqrsDispatcher
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public IAsyncEnumerable<TItem> Stream<TItem>(IStreamRequest<TItem> request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public IAsyncEnumerable<object?> Stream(object request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification
            => Task.CompletedTask;
    }
}

/// <summary>A command whose handler schedules a <see cref="ParallelProbe" />, then succeeds or returns a failed result.</summary>
public sealed class ScheduleProbeCommand : CommandBase
{
    public required int Seq { get; init; }
    public required DateTimeOffset DueAt { get; init; }
    public bool Fail { get; init; }
}

public sealed class ScheduleProbeCommandHandler(ICqrsDispatcher dispatcher) : ICommandHandler<ScheduleProbeCommand>
{
    public async Task<CommandResult> Handle(ScheduleProbeCommand command, CancellationToken cancellationToken)
    {
        await dispatcher.PublishAt(new ParallelProbe(command.Seq, null), command.DueAt, cancellationToken);
        return command.Fail ? CommandResult.FromError("declined") : CommandResult.FromSuccess();
    }
}
