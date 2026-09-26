using System.Diagnostics.Metrics;
using System.Threading.Channels;
using CQRSharp.Core.BackgroundTasks;
using CQRSharp.Core.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Core;

/// <summary>
///     <see cref="BackgroundTaskQueue" /> on its own: what each full mode does to whom, how a caller's cancellation and
///     the queue's closing settle a waiting item, and what its instruments report. The test plays the consumer, dequeuing
///     and running items itself, so every step is deterministic.
/// </summary>
public sealed class BackgroundTaskQueueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(DisplayName = "The queue's meter is created through the provider's IMeterFactory, which owns it, as the CQRSharp meter is")]
    public async Task Queue_meter_belongs_to_the_providers_meter_factory()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddCqrsGenerated();
        await using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IMeterFactory>();
        var meter = provider.GetRequiredService<BackgroundTaskQueue>().Meter;

        meter.Scope.Should().BeSameAs(factory, "a listener scoped to the provider's factory sees the queue's instruments");
        meter.Name.Should().Be(CqrsTelemetry.BackgroundTasksMeterName);
        meter.Version.Should().Be(provider.GetRequiredService<CqrsMetrics>().Meter.Version);
    }

    [Fact(DisplayName = "Items are dequeued in the order they were queued")]
    public async Task Items_are_dequeued_in_FIFO_order()
    {
        const int count = 50;
        using var queue = Queue(count, BoundedChannelFullMode.Wait);
        var ran = new List<int>();

        var tasks = Enumerable.Range(0, count)
            .Select(i => queue.EnqueueAsync(_ =>
            {
                ran.Add(i);
                return Task.CompletedTask;
            }, Ct))
            .ToArray();

        for (var i = 0; i < count; i++)
            await RunNextAsync(queue);

        await Task.WhenAll(tasks);
        ran.Should().Equal(Enumerable.Range(0, count));
    }

    [Fact(DisplayName = "DropWrite: a full queue refuses the new item at once, and its caller learns why")]
    public async Task DropWrite_refuses_the_new_item()
    {
        using var queue = Queue(2, BoundedChannelFullMode.DropWrite);
        using var probe = new QueueMeterProbe(queue.Meter);
        var ran = new List<string>();

        var a = queue.EnqueueAsync(Record(ran, "a"), Ct);
        var b = queue.EnqueueAsync(Record(ran, "b"), Ct);
        var c = queue.EnqueueAsync(Record(ran, "c"), Ct);

        c.IsFaulted.Should().BeTrue("a refused item is answered on the spot, not left pending");
        var refused = await Assert.ThrowsAsync<BackgroundTaskRejectedException>(() => c);
        refused.Reason.Should().Be(BackgroundTaskRejectionReason.QueueFull);
        refused.Message.Should().Contain("DropWrite");

        probe.Depth().Should().Be(2);
        probe.Count(CqrsTelemetry.QueueInstruments.Enqueued).Should().Be(2);
        probe.Count(CqrsTelemetry.QueueInstruments.Rejected, "full").Should().Be(1);
        probe.Count(CqrsTelemetry.QueueInstruments.Evicted).Should().Be(0);

        await RunNextAsync(queue);
        await RunNextAsync(queue);
        await Task.WhenAll(a, b);
        ran.Should().Equal("a", "b");
    }

    [Fact(DisplayName = "DropNewest: a full queue evicts the newest queued item, whose caller learns why")]
    public async Task DropNewest_evicts_the_newest_queued_item()
    {
        using var queue = Queue(2, BoundedChannelFullMode.DropNewest);
        using var probe = new QueueMeterProbe(queue.Meter);
        var ran = new List<string>();

        var a = queue.EnqueueAsync(Record(ran, "a"), Ct);
        var b = queue.EnqueueAsync(Record(ran, "b"), Ct);
        var c = queue.EnqueueAsync(Record(ran, "c"), Ct);

        var evicted = await Assert.ThrowsAsync<BackgroundTaskRejectedException>(() => b);
        evicted.Reason.Should().Be(BackgroundTaskRejectionReason.Evicted);
        evicted.Message.Should().Contain("DropNewest");

        probe.Depth().Should().Be(2, "the depth is the channel's own count, so an eviction cannot make it drift");
        probe.Count(CqrsTelemetry.QueueInstruments.Enqueued).Should().Be(3);
        probe.Count(CqrsTelemetry.QueueInstruments.Evicted, "drop_newest").Should().Be(1);
        probe.Count(CqrsTelemetry.QueueInstruments.Rejected).Should().Be(0);

        await RunNextAsync(queue);
        await RunNextAsync(queue);
        await Task.WhenAll(a, c);
        ran.Should().Equal("a", "c");
        probe.Depth().Should().Be(0);
    }

    [Fact(DisplayName = "DropOldest: a full queue evicts the oldest queued item, whose caller learns why")]
    public async Task DropOldest_evicts_the_oldest_queued_item()
    {
        using var queue = Queue(2, BoundedChannelFullMode.DropOldest);
        using var probe = new QueueMeterProbe(queue.Meter);
        var ran = new List<string>();

        var a = queue.EnqueueAsync(Record(ran, "a"), Ct);
        var b = queue.EnqueueAsync(Record(ran, "b"), Ct);
        var c = queue.EnqueueAsync(Record(ran, "c"), Ct);

        var evicted = await Assert.ThrowsAsync<BackgroundTaskRejectedException>(() => a);
        evicted.Reason.Should().Be(BackgroundTaskRejectionReason.Evicted);
        evicted.Message.Should().Contain("DropOldest");

        probe.Depth().Should().Be(2);
        probe.Count(CqrsTelemetry.QueueInstruments.Evicted, "drop_oldest").Should().Be(1);

        await RunNextAsync(queue);
        await RunNextAsync(queue);
        await Task.WhenAll(b, c);
        ran.Should().Equal("b", "c");
    }

    [Fact(DisplayName = "Wait: a producer waiting for room stops waiting when its own token is cancelled")]
    public async Task Wait_mode_producer_can_give_up()
    {
        using var queue = Queue(1, BoundedChannelFullMode.Wait);
        using var probe = new QueueMeterProbe(queue.Meter);
        _ = queue.EnqueueAsync(_ => Task.CompletedTask, Ct);

        using var giveUp = new CancellationTokenSource();
        var pending = queue.EnqueueAsync(_ => Task.CompletedTask, giveUp.Token);
        pending.IsCompleted.Should().BeFalse("the queue is full, so the producer waits for room");

        giveUp.Cancel();

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        cancelled.CancellationToken.Should().Be(giveUp.Token, "the caller observes the token it passed in");
        probe.Depth().Should().Be(1, "the withdrawn item never entered the queue");
        probe.Count(CqrsTelemetry.QueueInstruments.Enqueued).Should().Be(1);
    }

    [Fact(DisplayName = "Wait: a producer waiting for room is refused when the queue closes, so it does not hang")]
    public async Task Wait_mode_producer_is_refused_when_the_queue_closes()
    {
        using var queue = Queue(1, BoundedChannelFullMode.Wait);
        using var probe = new QueueMeterProbe(queue.Meter);
        _ = queue.EnqueueAsync(_ => Task.CompletedTask, Ct);

        var blocked = queue.EnqueueAsync(_ => Task.CompletedTask, Ct);
        blocked.IsCompleted.Should().BeFalse();

        ((IBackgroundTaskQueue)queue).CompleteAdding();

        var refused = await Assert.ThrowsAsync<BackgroundTaskRejectedException>(() => blocked.WaitAsync(Ct));
        refused.Reason.Should().Be(BackgroundTaskRejectionReason.QueueClosed);
        probe.Count(CqrsTelemetry.QueueInstruments.Rejected, "closed").Should().Be(1);
    }

    [Theory(DisplayName = "A closed queue refuses new work at once, whatever its full mode")]
    [InlineData(BoundedChannelFullMode.Wait)]
    [InlineData(BoundedChannelFullMode.DropWrite)]
    [InlineData(BoundedChannelFullMode.DropOldest)]
    [InlineData(BoundedChannelFullMode.DropNewest)]
    public async Task A_closed_queue_refuses_new_work(BoundedChannelFullMode fullMode)
    {
        using var queue = Queue(4, fullMode);
        using var probe = new QueueMeterProbe(queue.Meter);
        ((IBackgroundTaskQueue)queue).CompleteAdding();

        var late = queue.EnqueueAsync(_ => Task.FromResult(1), Ct);

        late.IsFaulted.Should().BeTrue();
        (await Assert.ThrowsAsync<BackgroundTaskRejectedException>(() => late)).Reason.Should().Be(BackgroundTaskRejectionReason.QueueClosed);
        probe.Count(CqrsTelemetry.QueueInstruments.Rejected, "closed").Should().Be(1);
        probe.Count(CqrsTelemetry.QueueInstruments.Rejected, "full").Should().Be(0, "a shutdown refusal is not a capacity refusal");
    }

    [Fact(DisplayName = "A caller that gives up while its item is queued is answered at once, and the item never runs")]
    public async Task A_queued_item_is_withdrawn_when_its_caller_gives_up()
    {
        using var queue = Queue(4, BoundedChannelFullMode.Wait);
        using var probe = new QueueMeterProbe(queue.Meter);
        using var giveUp = new CancellationTokenSource();
        var ran = false;

        var withdrawn = queue.EnqueueAsync(_ =>
        {
            ran = true;
            return Task.CompletedTask;
        }, giveUp.Token);
        var next = queue.EnqueueAsync(_ => Task.FromResult(42), Ct);

        giveUp.Cancel();

        withdrawn.IsCanceled.Should().BeTrue("the caller is answered when it gives up, not when the item reaches the head");
        (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => withdrawn)).CancellationToken.Should().Be(giveUp.Token);

        await RunNextAsync(queue);
        (await next).Should().Be(42, "the consumer skips the withdrawn item and runs the one behind it");
        ran.Should().BeFalse();
        probe.Depth().Should().Be(0);
    }

    [Fact(DisplayName = "Once an item has started, its caller's token no longer withdraws it: the work decides its outcome")]
    public async Task A_started_item_is_not_withdrawn()
    {
        using var queue = Queue(4, BoundedChannelFullMode.Wait);
        using var giveUp = new CancellationTokenSource();
        var result = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = queue.EnqueueAsync(_ => result.Task, giveUp.Token);
        var item = await ((IBackgroundTaskQueue)queue).DequeueAsync(Ct);
        giveUp.Cancel();

        task.IsCompleted.Should().BeFalse();
        var running = item!.RunAsync(CancellationToken.None);
        result.SetResult(7);
        await running;
        (await task).Should().Be(7);
    }

    [Fact(DisplayName = "A caller whose token has already fired is answered at once, and nothing is queued")]
    public async Task An_already_cancelled_caller_queues_nothing()
    {
        using var queue = Queue(4, BoundedChannelFullMode.Wait);
        using var probe = new QueueMeterProbe(queue.Meter);

        var task = queue.EnqueueAsync(_ => Task.CompletedTask, new CancellationToken(canceled: true));

        task.IsCanceled.Should().BeTrue();
        probe.Depth().Should().Be(0);
        probe.Count(CqrsTelemetry.QueueInstruments.Enqueued).Should().Be(0);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact(DisplayName = "A dequeue whose token has fired takes nothing, even when an item is ready")]
    public async Task A_cancelled_dequeue_takes_nothing()
    {
        using var queue = Queue(4, BoundedChannelFullMode.Wait);
        IBackgroundTaskQueue consumerSide = queue;
        var task = queue.EnqueueAsync(_ => Task.FromResult(1), Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => consumerSide.DequeueAsync(new CancellationToken(canceled: true)).AsTask());

        task.IsCompleted.Should().BeFalse("the item is still queued, not claimed and dropped");
        await RunNextAsync(queue);
        (await task).Should().Be(1);
    }

    [Fact(DisplayName = "Cancelling what is pending after the queue closes answers every caller and empties the queue")]
    public async Task Cancel_pending_empties_the_queue()
    {
        using var queue = Queue(4, BoundedChannelFullMode.Wait);
        using var probe = new QueueMeterProbe(queue.Meter);
        IBackgroundTaskQueue consumerSide = queue;

        var pending = Enumerable.Range(0, 3).Select(_ => queue.EnqueueAsync(_ => Task.CompletedTask, Ct)).ToArray();
        probe.Depth().Should().Be(3);

        consumerSide.CompleteAdding();
        consumerSide.CancelPending().Should().Be(3);

        probe.Depth().Should().Be(0);
        foreach (var task in pending)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        (await consumerSide.DequeueAsync(Ct)).Should().BeNull("a completed, empty queue has nothing more to hand out");
    }

    [Fact(DisplayName = "Disposing the queue cancels what it still holds and refuses what comes after")]
    public async Task Dispose_cancels_queued_work()
    {
        var queue = Queue(4, BoundedChannelFullMode.Wait);
        var a = queue.EnqueueAsync(_ => Task.CompletedTask, Ct);
        var b = queue.EnqueueAsync(_ => Task.FromResult(1), Ct);

        queue.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);
        var late = queue.EnqueueAsync(_ => Task.CompletedTask, Ct);
        (await Assert.ThrowsAsync<BackgroundTaskRejectedException>(() => late)).Reason.Should().Be(BackgroundTaskRejectionReason.QueueClosed);
    }

    [Fact(DisplayName = "The wait histogram records how long an item waited, on the queue's clock")]
    public async Task Wait_duration_is_measured_on_the_queue_clock()
    {
        var time = new FakeTimeProvider();
        using var queue = Queue(4, BoundedChannelFullMode.Wait, time);
        using var probe = new QueueMeterProbe(queue.Meter);

        var task = queue.EnqueueAsync(_ => Task.CompletedTask, Ct);
        time.Advance(TimeSpan.FromSeconds(3));
        await RunNextAsync(queue);
        await task;

        probe.Values(CqrsTelemetry.QueueInstruments.WaitDuration).Should().Equal(3.0);
    }

    [Fact(DisplayName = "The queue publishes exactly the instruments CqrsTelemetry names, on the meter it names")]
    public void Published_names_are_the_real_names()
    {
        using var queue = Queue(4, BoundedChannelFullMode.Wait);
        using var probe = new QueueMeterProbe(queue.Meter);

        queue.Meter.Name.Should().Be(CqrsTelemetry.BackgroundTasksMeterName);
        CqrsTelemetry.MeterNames.Should().Contain(CqrsTelemetry.BackgroundTasksMeterName);
        probe.Instruments.Should().BeEquivalentTo(
        [
            CqrsTelemetry.QueueInstruments.Depth,
            CqrsTelemetry.QueueInstruments.Enqueued,
            CqrsTelemetry.QueueInstruments.Evicted,
            CqrsTelemetry.QueueInstruments.Rejected,
            CqrsTelemetry.QueueInstruments.WaitDuration
        ]);
    }

    [Fact(DisplayName = "Disposing one queue does not stop another queue's instruments")]
    public void Each_queue_owns_its_meter()
    {
        var first = Queue(4, BoundedChannelFullMode.Wait);
        using var second = Queue(4, BoundedChannelFullMode.Wait);
        using var probe = new QueueMeterProbe(second.Meter);

        first.Dispose();
        _ = second.EnqueueAsync(_ => Task.CompletedTask, Ct);

        probe.Count(CqrsTelemetry.QueueInstruments.Enqueued).Should().Be(1);
        probe.Depth().Should().Be(1);
    }

    [Fact(DisplayName = "The default shutdown budget leaves the host's own budget room for the other hosted services")]
    public void Default_shutdown_timeout_is_below_the_hosts()
        => new BackgroundTaskQueueOptions().ShutdownTimeout.Should().BeLessThan(new HostOptions().ShutdownTimeout);

    private static BackgroundTaskQueue Queue(int capacity, BoundedChannelFullMode fullMode, TimeProvider? timeProvider = null)
        => new(Options.Create(new BackgroundTaskQueueOptions { Capacity = capacity, FullMode = fullMode }), timeProvider);

    private static Func<CancellationToken, Task> Record(List<string> ran, string name) => _ =>
    {
        ran.Add(name);
        return Task.CompletedTask;
    };

    // Plays the consumer: takes the next item and runs it to completion on the test's own flow.
    private static async Task RunNextAsync(IBackgroundTaskQueue queue)
    {
        var item = await queue.DequeueAsync(Ct);
        item.Should().NotBeNull();
        await item!.RunAsync(CancellationToken.None);
    }
}
