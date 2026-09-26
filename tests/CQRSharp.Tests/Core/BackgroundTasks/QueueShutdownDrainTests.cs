using CQRSharp.Core.BackgroundTasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Core;

/// <summary>
///     What happens to work that is still <em>queued</em> — not yet executing — when the host stops. With one consumer
///     slot held by a gated item, everything enqueued behind it is backlog at the moment shutdown begins. The shutdown
///     budget runs on a fake clock, and <see cref="ObservedQueue" /> signals the moment the consumer closes the queue,
///     which is when the drain begins.
/// </summary>
public sealed class QueueShutdownDrainTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(DisplayName = "Shutdown runs the queued backlog before the consumer stops")]
    public async Task Queued_work_is_drained()
    {
        var shutdown = Create(drainOnShutdown: true);
        await shutdown.Consumer.StartAsync(Ct);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (first, running) = EnqueueGated(shutdown.Queue, gate.Task);
        await running.WaitAsync(Ct);

        var ran = 0;
        var backlog = Enumerable.Range(0, 5)
            .Select(_ => shutdown.Queue.EnqueueAsync(_ =>
            {
                Interlocked.Increment(ref ran);
                return Task.CompletedTask;
            }, Ct))
            .ToArray();

        var stopping = shutdown.Consumer.StopAsync(CancellationToken.None);
        gate.SetResult();
        await stopping.WaitAsync(Ct);

        await first;
        await Task.WhenAll(backlog);
        ran.Should().Be(5);

        shutdown.Dispose();
    }

    [Fact(DisplayName = "Shutdown refuses new work while it is still draining")]
    public async Task New_work_is_refused_during_the_drain()
    {
        var shutdown = Create(drainOnShutdown: true);
        await shutdown.Consumer.StartAsync(Ct);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (first, running) = EnqueueGated(shutdown.Queue, gate.Task);
        await running.WaitAsync(Ct);

        var stopping = shutdown.Consumer.StopAsync(CancellationToken.None);
        await shutdown.Observed.Closed.WaitAsync(Ct);

        var late = shutdown.Queue.EnqueueAsync(_ => Task.CompletedTask, Ct);

        late.IsFaulted.Should().BeTrue("the queue closes before the drain starts, not after it");
        stopping.IsCompleted.Should().BeFalse("the drain is still waiting for the gated item");
        (await Assert.ThrowsAsync<BackgroundTaskRejectedException>(() => late)).Reason.Should().Be(BackgroundTaskRejectionReason.QueueClosed);

        gate.SetResult();
        await stopping.WaitAsync(Ct);
        await first;

        shutdown.Dispose();
    }

    [Fact(DisplayName = "Backlog the shutdown budget does not cover is cancelled, so its callers do not hang")]
    public async Task Backlog_past_the_deadline_is_cancelled()
    {
        var shutdown = Create(drainOnShutdown: true);
        await shutdown.Consumer.StartAsync(Ct);

        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = shutdown.Queue.EnqueueAsync(async ct =>
        {
            running.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, Ct);
        await running.Task.WaitAsync(Ct);

        var ran = false;
        var queued = shutdown.Queue.EnqueueAsync(_ =>
        {
            ran = true;
            return Task.CompletedTask;
        }, Ct);

        var stopping = shutdown.Consumer.StopAsync(CancellationToken.None);

        // The budget's timer exists by the time the queue closes; spending it ends the drain.
        await shutdown.Observed.Closed.WaitAsync(Ct);
        shutdown.Time.Advance(Budget);
        await stopping.WaitAsync(Ct);

        await ((Func<Task>)(() => first)).Should().ThrowAsync<OperationCanceledException>("in-flight work is cancelled once the budget is spent");
        await ((Func<Task>)(() => queued)).Should().ThrowAsync<OperationCanceledException>("queued work that never ran is cancelled, not left pending");
        ran.Should().BeFalse();

        shutdown.Dispose();
    }

    [Fact(DisplayName = "DrainOnShutdown = false cancels the backlog at once, while in-flight work still holds its slot, and lets that work finish")]
    public async Task Drain_can_be_turned_off()
    {
        var shutdown = Create(drainOnShutdown: false);
        await shutdown.Consumer.StartAsync(Ct);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (first, running) = EnqueueGated(shutdown.Queue, gate.Task);
        await running.WaitAsync(Ct);

        var ran = false;
        var queued = shutdown.Queue.EnqueueAsync(_ =>
        {
            ran = true;
            return Task.CompletedTask;
        }, Ct);

        var stopping = shutdown.Consumer.StopAsync(CancellationToken.None);

        // The budget runs on the fake clock and the gate is closed, so only the immediate cancellation can end the wait;
        // the real-time bound only keeps a regression from hanging the run.
        await ((Func<Task>)(() => queued.WaitAsync(TimeSpan.FromSeconds(30), Ct))).Should().ThrowAsync<OperationCanceledException>(
            "nothing will run the backlog, so its callers are released before the running work finishes");
        stopping.IsCompleted.Should().BeFalse("the in-flight work still holds its slot");

        gate.SetResult();
        await stopping.WaitAsync(Ct);
        await first;
        ran.Should().BeFalse();

        shutdown.Dispose();
    }

    private static (Task Work, Task Running) EnqueueGated(BackgroundTaskQueue queue, Task gate)
    {
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = queue.EnqueueAsync(async _ =>
        {
            running.SetResult();
            await gate;
        }, Ct);
        return (work, running.Task);
    }

    private static Shutdown Create(bool drainOnShutdown)
    {
        var options = Options.Create(new BackgroundTaskQueueOptions
        {
            Capacity = 16,
            ConsumerCount = 1,
            ShutdownTimeout = Budget,
            DrainOnShutdown = drainOnShutdown
        });
        var time = new FakeTimeProvider();
        var queue = new BackgroundTaskQueue(options, time);
        var observed = new ObservedQueue(queue);
        var consumer = new BackgroundTaskQueueConsumer(observed, NullLogger<BackgroundTaskQueueConsumer>.Instance, options, new ConsumerReadiness(), time);
        return new Shutdown(queue, observed, consumer, time);
    }

    private sealed record Shutdown(BackgroundTaskQueue Queue, ObservedQueue Observed, BackgroundTaskQueueConsumer Consumer, FakeTimeProvider Time) : IDisposable
    {
        public void Dispose()
        {
            Consumer.Dispose();
            Queue.Dispose();
        }
    }
}
