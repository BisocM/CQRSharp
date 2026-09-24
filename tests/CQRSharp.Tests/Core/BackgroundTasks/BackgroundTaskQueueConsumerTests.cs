using System.Collections.Concurrent;
using CQRSharp.Core.BackgroundTasks;
using CQRSharp.Core.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Core;

/// <summary>
///     <see cref="BackgroundTaskQueueConsumer" /> running a real queue: concurrency, how a work item's outcome reaches
///     its caller, and a forced stop. The shutdown budget runs on a fake clock that no test advances, so nothing here
///     waits for a timer.
/// </summary>
public sealed class BackgroundTaskQueueConsumerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(DisplayName = "ConsumerCount items run at the same time, even when they block before their first await")]
    public async Task Consumer_count_items_run_concurrently()
    {
        var (queue, consumer) = Create(consumerCount: 2);
        await consumer.StartAsync(Ct);

        // Each item blocks its thread until both have started: run one at a time, the first would never return.
        using var bothStarted = new CountdownEvent(2);
        Func<CancellationToken, Task> blocking = _ =>
        {
            bothStarted.Signal();
            bothStarted.Wait(Ct);
            return Task.CompletedTask;
        };

        var first = queue.EnqueueAsync(blocking, Ct);
        var second = queue.EnqueueAsync(blocking, Ct);

        await Task.WhenAll(first, second).WaitAsync(Ct);

        await StopAsync(queue, consumer);
    }

    [Fact(DisplayName = "A work item that throws or cancels itself faults or cancels its caller's task, and the next item still runs")]
    public async Task Work_item_outcomes_reach_their_callers()
    {
        var (queue, consumer) = Create(consumerCount: 1);
        await consumer.StartAsync(Ct);

        using var own = new CancellationTokenSource();
        await own.CancelAsync();

        var throwing = queue.EnqueueAsync(_ => Task.FromException(new InvalidOperationException("work failed")), Ct);
        var cancelling = queue.EnqueueAsync<int>(_ => throw new OperationCanceledException(own.Token), Ct);
        var next = queue.EnqueueAsync(_ => Task.FromResult(42), Ct);

        (await Assert.ThrowsAsync<InvalidOperationException>(() => throwing.WaitAsync(Ct))).Message.Should().Be("work failed");
        (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelling)).CancellationToken.Should().Be(own.Token);
        (await next.WaitAsync(Ct)).Should().Be(42, "a failed item gives its slot back like any other");

        await StopAsync(queue, consumer);
    }

    [Fact(DisplayName = "A forced stop cancels the work still running")]
    public async Task A_forced_stop_cancels_running_work()
    {
        var (queue, consumer) = Create(consumerCount: 1);
        await consumer.StartAsync(Ct);

        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = queue.EnqueueAsync(async ct =>
        {
            running.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, Ct);
        await running.Task.WaitAsync(Ct);

        await consumer.StopAsync(new CancellationToken(canceled: true)).WaitAsync(Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight);
        consumer.Dispose();
        queue.Dispose();
    }

    [Fact(DisplayName = "Wait mode stays consistent with several producers and consumers")]
    public async Task Wait_mode_with_several_producers_and_consumers()
    {
        const int producers = 5;
        const int itemsPerProducer = 100;
        var (queue, consumer) = Create(consumerCount: 3, capacity: 50);
        using var probe = new QueueMeterProbe(queue.Meter);
        await consumer.StartAsync(Ct);

        var processed = new ConcurrentBag<int>();
        var producing = Enumerable.Range(0, producers).Select(p => Task.Run(async () =>
        {
            for (var i = 0; i < itemsPerProducer; i++)
            {
                var value = p * itemsPerProducer + i;
                await queue.EnqueueAsync(_ =>
                {
                    processed.Add(value);
                    return Task.CompletedTask;
                }, Ct);
            }
        }, Ct));
        await Task.WhenAll(producing).WaitAsync(Ct);

        await StopAsync(queue, consumer);

        processed.Should().HaveCount(producers * itemsPerProducer).And.OnlyHaveUniqueItems();
        probe.Count(CqrsTelemetry.QueueInstruments.Enqueued).Should().Be(producers * itemsPerProducer);
    }

    [Fact(DisplayName = "A work item knows it runs on its queue; its caller and other queues do not")]
    public async Task Work_items_are_marked_as_running_on_their_queue()
    {
        var (queue, consumer) = Create(consumerCount: 1);
        using var other = new BackgroundTaskQueue(Options.Create(new BackgroundTaskQueueOptions()));
        await consumer.StartAsync(Ct);

        var marks = await queue.EnqueueAsync(_ => Task.FromResult((
            Own: BackgroundTaskQueueConsumer.IsRunningWorkItemOf(queue),
            Other: BackgroundTaskQueueConsumer.IsRunningWorkItemOf(other))), Ct).WaitAsync(Ct);

        marks.Own.Should().BeTrue();
        marks.Other.Should().BeFalse();
        BackgroundTaskQueueConsumer.IsRunningWorkItemOf(queue).Should().BeFalse("the mark stays inside the item's own flow");

        await StopAsync(queue, consumer);
    }

    private static (BackgroundTaskQueue Queue, BackgroundTaskQueueConsumer Consumer) Create(int consumerCount, int capacity = 16)
    {
        var options = Options.Create(new BackgroundTaskQueueOptions
        {
            Capacity = capacity,
            ConsumerCount = consumerCount,
            ShutdownTimeout = TimeSpan.FromSeconds(30)
        });
        var queue = new BackgroundTaskQueue(options);
        var consumer = new BackgroundTaskQueueConsumer(
            queue, NullLogger<BackgroundTaskQueueConsumer>.Instance, options, new ConsumerReadiness(), new FakeTimeProvider());
        return (queue, consumer);
    }

    private static async Task StopAsync(BackgroundTaskQueue queue, BackgroundTaskQueueConsumer consumer)
    {
        await consumer.StopAsync(CancellationToken.None).WaitAsync(Ct);
        consumer.Dispose();
        queue.Dispose();
    }
}
