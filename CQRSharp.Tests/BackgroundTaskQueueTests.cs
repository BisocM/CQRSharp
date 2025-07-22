using System.Threading.Channels;
using CQRSharp.Core.BackgroundTasks;
using CQRSharp.Core.BackgroundTasks.Telemetry;
using CQRSharp.Core.BackgroundTasks.Types;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Options;
using CQRSharp.Abstractions.Data.Interfaces.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CQRSharp.Tests;

public class BackgroundTaskQueueTests
{
    // Helper to create BackgroundTaskQueueOptions with the given capacity and full-mode
    private static BackgroundTaskQueueOptions MakeOptions(int capacity, BoundedChannelFullMode fullMode)
    {
        return new BackgroundTaskQueueOptions
        {
            Capacity = capacity,
            FullMode = fullMode,
            ConsumerCount = 1
        };
    }

    private static IOptions<BackgroundTaskQueueOptions> Opts(BackgroundTaskQueueOptions o)
        => new TestOptions(o);

    // Constructs a BackgroundTaskQueue with no-op dispatcher and logger, plus test metrics
    private (BackgroundTaskQueue Queue, TestMetricsReporter Metrics) CreateQueue(BackgroundTaskQueueOptions opts)
    {
        var dispatcher = new NoOpDispatcher();
        var lifetime = new TestHostApplicationLifetime();
        var metrics = new TestMetricsReporter();
        var logger = NullLogger<BackgroundTaskQueue>.Instance;
        var queue = new BackgroundTaskQueue(Opts(opts), dispatcher, lifetime, metrics, logger);
        return (queue, metrics);
    }

    [Fact(DisplayName = "Bounded queue preserves FIFO order and never drops items")]
    public async Task BoundedQueue_PreservesOrderAndNoDrops()
    {
        const int itemCount = 50;
        var opts = MakeOptions(itemCount, BoundedChannelFullMode.Wait);
        var (queue, _) = CreateQueue(opts);
        var ct = CancellationToken.None;

        // Enqueue a sequence of work items
        var items = Enumerable.Range(1, itemCount)
            .Select(i => (Func<CancellationToken, Task>)(_ => Task.CompletedTask))
            .ToList();

        foreach (var w in items)
            await queue.QueueBackgroundWorkItemAsync(w, ct);

        // Dequeue them all and verify order
        var drained = new List<Func<CancellationToken, Task>>();
        for (var i = 0; i < items.Count; i++)
        {
            var queued = await queue.DequeueAsync(ct);
            drained.Add(queued.WorkItem);
        }

        Assert.Equal(items.Count, drained.Count);
        Assert.True(
            drained.SequenceEqual(items),
            "Bounded queue should return items in the exact order enqueued."
        );
    }

    [Fact(DisplayName = "DropNewest policy always reports Enqueued and evicts prior newest")]
    public async Task DropNewest_AlwaysEnqueuesAndEvictsNewest()
    {
        var opts = MakeOptions(2, BoundedChannelFullMode.DropNewest);
        var (queue, metrics) = CreateQueue(opts);
        var ct = CancellationToken.None;

        var A = (Func<CancellationToken, Task>)(_ => Task.CompletedTask);
        var B = (Func<CancellationToken, Task>)(_ => Task.CompletedTask);
        var C = (Func<CancellationToken, Task>)(_ => Task.CompletedTask);

        // Fill
        var rA = await queue.QueueBackgroundWorkItemAsync(A, ct);
        var rB = await queue.QueueBackgroundWorkItemAsync(B, ct);
        // This will *always* return Enqueued
        var rC = await queue.QueueBackgroundWorkItemAsync(C, ct);

        Assert.Equal(QueueWriteResultCode.Enqueued, rA.Result);
        Assert.Equal(QueueWriteResultCode.Enqueued, rB.Result);
        Assert.Equal(QueueWriteResultCode.Enqueued, rC.Result);

        // Metrics count *all* writes as enqueued, never record a drop
        Assert.Equal(3, metrics.EnqueuedCount);
        Assert.Equal(0, metrics.DroppedNewestCount);

        // The *previous* newest (B) got evicted; remaining items are A and C
        var got = new[]
        {
            (await queue.DequeueAsync(ct)).WorkItem,
            (await queue.DequeueAsync(ct)).WorkItem
        };
        Assert.Contains(A, got);
        Assert.Contains(C, got);
        Assert.DoesNotContain(B, got);
    }

    [Fact(DisplayName = "DropWrite policy always reports Enqueued but leaves queue unchanged")]
    public async Task DropWrite_AlwaysEnqueuesAndDropsIncoming()
    {
        var opts = MakeOptions(2, BoundedChannelFullMode.DropWrite);
        var (queue, metrics) = CreateQueue(opts);
        var ct = CancellationToken.None;

        var A = (Func<CancellationToken, Task>)(_ => Task.CompletedTask);
        var B = (Func<CancellationToken, Task>)(_ => Task.CompletedTask);
        var C = (Func<CancellationToken, Task>)(_ => Task.CompletedTask);

        // Fill
        var rA = await queue.QueueBackgroundWorkItemAsync(A, ct);
        var rB = await queue.QueueBackgroundWorkItemAsync(B, ct);
        // Always succeeds, but C is never enqueued
        var rC = await queue.QueueBackgroundWorkItemAsync(C, ct);

        Assert.Equal(QueueWriteResultCode.Enqueued, rA.Result);
        Assert.Equal(QueueWriteResultCode.Enqueued, rB.Result);
        Assert.Equal(QueueWriteResultCode.Enqueued, rC.Result);

        // Metrics: all 3 writes recorded, no drops
        Assert.Equal(3, metrics.EnqueuedCount);
        Assert.Equal(0, metrics.DroppedNewestCount);

        // Queue still only holds A and B
        var got = new[]
        {
            (await queue.DequeueAsync(ct)).WorkItem,
            (await queue.DequeueAsync(ct)).WorkItem
        };
        Assert.Contains(A, got);
        Assert.Contains(B, got);
        Assert.DoesNotContain(C, got);
    }

    [Fact(DisplayName = "DropOldest policy replaces oldest when full")]
    public async Task DropOldest_ReplacesOldestWhenFull()
    {
        var opts = MakeOptions(2, BoundedChannelFullMode.DropOldest);
        var (queue, metrics) = CreateQueue(opts);
        var ct = CancellationToken.None;

        var first = (Func<CancellationToken, Task>)(_ => Task.CompletedTask);
        var second = (Func<CancellationToken, Task>)(_ => Task.CompletedTask);
        var third = (Func<CancellationToken, Task>)(_ => Task.CompletedTask);

        // Enqueue three; oldest should be evicted when adding third
        await queue.QueueBackgroundWorkItemAsync(first, ct);
        await queue.QueueBackgroundWorkItemAsync(second, ct);
        var result = await queue.QueueBackgroundWorkItemAsync(third, ct);

        Assert.Equal(QueueWriteResultCode.Enqueued, result.Result);
        Assert.Equal(0, metrics.DroppedOldestCount);

        // Remaining should be [second, third]
        var got = new[]
        {
            (await queue.DequeueAsync(ct)).WorkItem,
            (await queue.DequeueAsync(ct)).WorkItem
        };
        Assert.Equal(new[] { second, third }, got);
    }

    [Fact(DisplayName = "Wait policy blocks and throws when canceled")]
    public async Task WaitMode_CancelsWhenCancelledBeforeSpaceAvailable()
    {
        var opts = MakeOptions(1, BoundedChannelFullMode.Wait);
        var (queue, _) = CreateQueue(opts);

        var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Fill once
        await queue.QueueBackgroundWorkItemAsync(_ => Task.CompletedTask, ct);

        // Cancel before second enqueue
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => queue.QueueBackgroundWorkItemAsync(_ => Task.CompletedTask, ct)
        );
    }

    [Fact(DisplayName = "DequeueAsync throws when canceled immediately")]
    public async Task DequeueAsync_RespectsImmediateCancellation()
    {
        var opts = MakeOptions(1, BoundedChannelFullMode.Wait);
        var (queue, _) = CreateQueue(opts);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => queue.DequeueAsync(cts.Token).AsTask()
        );
    }

    [Fact(DisplayName = "Stress test under DropNewest with high concurrency and metrics")]
    public async Task StressTest_HighConcurrencyDropNewest()
    {
        const int capacity = 100;
        const int producerCount = 1000;
        var opts = MakeOptions(capacity, BoundedChannelFullMode.DropNewest);
        var (queue, metrics) = CreateQueue(opts);
        var ct = CancellationToken.None;

        // Fill to capacity with long-running tasks
        for (var i = 0; i < capacity; i++)
            await queue.QueueBackgroundWorkItemAsync(_ => Task.Delay(TimeSpan.FromSeconds(10)), ct);

        // Fire off many producers in parallel—none will ever block or be rejected
        var writeTasks = Enumerable.Range(0, producerCount)
            .Select(_ => queue.QueueBackgroundWorkItemAsync(_ => Task.CompletedTask, ct))
            .ToArray();
        var results = await Task.WhenAll(writeTasks);

        // Verify ALL of them reported Enqueued
        Assert.All(results, r => Assert.Equal(QueueWriteResultCode.Enqueued, r.Result));

        // Metrics: initial fill + all 1000 producers
        Assert.Equal(capacity + producerCount, metrics.EnqueuedCount);
        Assert.Equal(0, metrics.DroppedNewestCount);

        // Drain exactly 'capacity' items to ensure the queue remains full of the most recent items
        var drained = 0;
        while (drained < capacity)
        {
            await queue.DequeueAsync(ct);
            drained++;
        }

        Assert.Equal(capacity, drained);
    }

    [Fact(DisplayName = "Sequential enqueues under Wait mode with large capacity")]
    public async Task HighCapacity_SequentialEnqueue()
    {
        const int tasksToProduce = 100_000;
        var opts = MakeOptions(tasksToProduce, BoundedChannelFullMode.Wait);
        var (queue, metrics) = CreateQueue(opts);
        var ct = CancellationToken.None;

        // Enqueue sequentially to avoid blocking
        for (var i = 0; i < tasksToProduce; i++)
        {
            var res = await queue.QueueBackgroundWorkItemAsync(_ => Task.CompletedTask, ct);
            Assert.Equal(QueueWriteResultCode.Enqueued, res.Result);
        }

        // All items should have been enqueued
        Assert.Equal(tasksToProduce, metrics.EnqueuedCount);

        // Drain all to verify queue is full of them
        var drained = 0;
        while (drained < tasksToProduce)
        {
            await queue.DequeueAsync(ct);
            drained++;
        }

        Assert.Equal(tasksToProduce, drained);
    }

    // Test metrics reporter to capture metric invocations
    private class TestMetricsReporter : IQueueMetricsReporter
    {
        public int EnqueuedCount { get; private set; }
        public int DroppedNewestCount { get; private set; }
        public int DroppedOldestCount { get; private set; }

        public void ItemEnqueued() => EnqueuedCount++;
        public void ItemDroppedNewest() => DroppedNewestCount++;
        public void ItemDroppedOldest() => DroppedOldestCount++;
        public long CurrentCount { get; }

        public void RecordLatency(TimeSpan latency)
        {
        }

        public void Dispose()
        {
        }
    }

    // A simple IOptions<T> wrapper for supplying test configuration
    private class TestOptions : IOptions<BackgroundTaskQueueOptions>
    {
        public TestOptions(BackgroundTaskQueueOptions options)
            => Value = options ?? throw new ArgumentNullException(nameof(options));

        public BackgroundTaskQueueOptions Value { get; }
    }

    // A fake IHostApplicationLifetime that never triggers shutdown unless StopApplication is called
    private class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _cts = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _cts.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _cts.Cancel();
    }

    // A no-op notification dispatcher to satisfy dependencies
    private class NoOpDispatcher : INotificationDispatcher
    {
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
            => Task.CompletedTask;
    }
}