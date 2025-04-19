using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CQRSharp.Core.BackgroundTasks;
using CQRSharp.Core.BackgroundTasks.Types;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Options;
using CQRSharp.Shared.Data.Interfaces.Notifications;

namespace CQRSharp.Tests
{
    public class BackgroundTaskQueueTests
    {
        // A simple IOptions<T> wrapper for supplying test configuration
        private class TestOptions : IOptions<BackgroundTaskQueueOptions>
        {
            public BackgroundTaskQueueOptions Value { get; }
            public TestOptions(BackgroundTaskQueueOptions options) =>
                Value = options ?? throw new ArgumentNullException(nameof(options));
        }

        // A fake IHostApplicationLifetime that never triggers shutdown unless StopApplication is called
        private class TestHostApplicationLifetime : IHostApplicationLifetime
        {
            private readonly CancellationTokenSource _cts = new();
            public CancellationToken ApplicationStarted  => CancellationToken.None;
            public CancellationToken ApplicationStopping => _cts.Token;
            public CancellationToken ApplicationStopped  => CancellationToken.None;
            public void StopApplication() => _cts.Cancel();
        }

        // A no-op notification dispatcher to satisfy dependencies
        private class NoOpDispatcher : INotificationDispatcher
        {
            public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification
            {
                return Task.CompletedTask;
            }
        }

        // Helper to create BackgroundTaskQueueOptions with the given capacity and full-mode
        private static BackgroundTaskQueueOptions MakeOptions(int capacity, BoundedChannelFullMode fullMode) =>
            new()
            {
                Capacity         = capacity,
                FullMode         = fullMode,
                ConsumerCount    = 1,
                DequeueBatchSize = 1
                // CallbackChannelCapacity uses the default
            };

        private static IOptions<BackgroundTaskQueueOptions> Opts(BackgroundTaskQueueOptions o) =>
            new TestOptions(o);

        // Constructs a BackgroundTaskQueue with no-op dispatcher and logger
        private BackgroundTaskQueue CreateQueue(BackgroundTaskQueueOptions opts)
        {
            var dispatcher = new NoOpDispatcher();
            var lifetime   = new TestHostApplicationLifetime();
            var logger     = NullLogger<BackgroundTaskQueue>.Instance;
            return new BackgroundTaskQueue(Opts(opts), dispatcher, lifetime, logger);
        }

        [Fact(DisplayName = "Bounded queue preserves FIFO order and never drops items")]
        public async Task BoundedQueue_PreservesOrderAndNoDrops()
        {
            const int itemCount = 50;
            var opts  = MakeOptions(capacity: itemCount, fullMode: BoundedChannelFullMode.Wait);
            var queue = CreateQueue(opts);
            var ct    = CancellationToken.None;

            // Enqueue a sequence of work items
            var items = Enumerable.Range(1, itemCount)
                                  .Select(i => (Func<CancellationToken, Task>)(_ => Task.CompletedTask))
                                  .ToList();

            foreach (var w in items)
                await queue.QueueBackgroundWorkItemAsync(w, ct);

            // Dequeue them all and verify order
            var drained = new List<Func<CancellationToken, Task>>();
            for (int i = 0; i < items.Count; i++)
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

        [Fact(DisplayName = "DropNewest policy evicts oldest item once capacity is exceeded")]
        public async Task DropNewest_EvictsOldestBeyondCapacity()
        {
            var opts  = MakeOptions(capacity: 2, fullMode: BoundedChannelFullMode.DropNewest);
            var queue = CreateQueue(opts);
            var ct    = CancellationToken.None;

            // Prepare three distinct delegates
            var A = (Func<CancellationToken, Task>)(_ => Task.CompletedTask);
            var B = (Func<CancellationToken, Task>)(_ => Task.CompletedTask);
            var C = (Func<CancellationToken, Task>)(_ => Task.CompletedTask);

            // Enqueue A and B
            var resA = await queue.QueueBackgroundWorkItemAsync(A, ct);
            var resB = await queue.QueueBackgroundWorkItemAsync(B, ct);
            // This third should *succeed* under DropNewest (evicting B)
            var resC = await queue.QueueBackgroundWorkItemAsync(C, ct);

            Assert.Equal(QueueWriteResultCode.Enqueued, resA.Result);
            Assert.Equal(QueueWriteResultCode.Enqueued, resB.Result);
            Assert.Equal(QueueWriteResultCode.Enqueued, resC.Result);

            // Metrics: 3 enqueues, no "rejected" counts under DropNewest
            Assert.Equal(3, queue.TotalItemsEnqueued);
            Assert.Equal(0, queue.TotalDroppedNewest);

            // Dequeue the two remaining work‑items
            var got = new[]
            {
                (await queue.DequeueAsync(ct)).WorkItem,
                (await queue.DequeueAsync(ct)).WorkItem
            };

            // A (the oldest) should remain, and C (the newest) should have replaced B
            Assert.Contains(A, got);
            Assert.Contains(C, got);
            Assert.DoesNotContain(B, got);
        }

        [Fact(DisplayName = "DropWrite policy silently drops incoming items when full")]
        public async Task DropWrite_SilentlyDropsIncomingItemsWhenFull()
        {
            var opts  = MakeOptions(capacity: 2, fullMode: BoundedChannelFullMode.DropWrite);
            var queue = CreateQueue(opts);
            var ct    = CancellationToken.None;

            var A = (Func<CancellationToken, Task>)(_ => Task.CompletedTask);
            var B = (Func<CancellationToken, Task>)(_ => Task.CompletedTask);
            var C = (Func<CancellationToken, Task>)(_ => Task.CompletedTask);

            // Enqueue A and B
            var resA = await queue.QueueBackgroundWorkItemAsync(A, ct);
            var resB = await queue.QueueBackgroundWorkItemAsync(B, ct);

            // Enqueue C: under DropWrite, it's dropped internally but TryWrite returns true
            var resC = await queue.QueueBackgroundWorkItemAsync(C, ct);

            // All calls return "Enqueued"
            Assert.Equal(QueueWriteResultCode.Enqueued, resA.Result);
            Assert.Equal(QueueWriteResultCode.Enqueued, resB.Result);
            Assert.Equal(QueueWriteResultCode.Enqueued, resC.Result);

            // Metrics reflect three attempts, no drops recorded by the queue itself
            Assert.Equal(3, queue.TotalItemsEnqueued);
            Assert.Equal(0, queue.TotalDroppedNewest);

            // Dequeue the two items that remain: should be A and B only
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
            var opts  = MakeOptions(capacity: 2, fullMode: BoundedChannelFullMode.DropOldest);
            var queue = CreateQueue(opts);
            var ct    = CancellationToken.None;

            var first   = (Func<CancellationToken, Task>)(_ => Task.CompletedTask);
            var second  = (Func<CancellationToken, Task>)(_ => Task.CompletedTask);
            var third   = (Func<CancellationToken, Task>)(_ => Task.CompletedTask);

            // Enqueue three; oldest should be dropped when adding third
            await queue.QueueBackgroundWorkItemAsync(first,  ct);
            await queue.QueueBackgroundWorkItemAsync(second, ct);
            var result = await queue.QueueBackgroundWorkItemAsync(third,  ct);

            Assert.Equal(QueueWriteResultCode.DroppedOldest, result.Result);
            Assert.Equal(1, queue.TotalDroppedOldest);

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
            var opts  = MakeOptions(capacity: 1, fullMode: BoundedChannelFullMode.Wait);
            var queue = CreateQueue(opts);

            var cts = new CancellationTokenSource();
            var ct  = cts.Token;

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
            var opts  = MakeOptions(capacity: 1, fullMode: BoundedChannelFullMode.Wait);
            var queue = CreateQueue(opts);
            var cts   = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<TaskCanceledException>(
                () => queue.DequeueAsync(cts.Token).AsTask()
            );
        }

        [Fact(DisplayName = "Stress test under DropNewest with high concurrency and metrics")]
        public async Task StressTest_HighConcurrencyDropNewest()
        {
            const int capacity      = 100;
            const int producerCount = 1000;
            var opts  = MakeOptions(capacity, BoundedChannelFullMode.DropNewest);
            var queue = CreateQueue(opts);
            var ct    = CancellationToken.None;

            // Fill to capacity with long‑running tasks
            for (int i = 0; i < capacity; i++)
                await queue.QueueBackgroundWorkItemAsync(_ => Task.Delay(TimeSpan.FromSeconds(10)), ct);

            // Fire off many producers in parallel—none should ever be rejected
            var writeTasks = Enumerable.Range(0, producerCount)
                .Select(_ => queue.QueueBackgroundWorkItemAsync(_ => Task.CompletedTask, ct))
                .ToArray();
            var results = await Task.WhenAll(writeTasks);

            // All writes should report Enqueued
            Assert.All(results, r => Assert.Equal(QueueWriteResultCode.Enqueued, r.Result));

            // Metrics: initial + producers all counted as enqueued, no rejects
            Assert.Equal(capacity + producerCount, queue.TotalItemsEnqueued);
            Assert.Equal(0, queue.TotalDroppedNewest);

            // Drain exactly 'capacity' items to ensure the queue remains full
            int drained = 0;
            while (drained < capacity)
            {
                await queue.DequeueAsync(ct);
                drained++;
            }
            Assert.Equal(capacity, drained);
        }

        [Fact(DisplayName = "Stress test under high concurrency with large capacity")]
        public async Task StressTest_HighCapacityHighConcurrency()
        {
            const int tasksToProduce = 100_000;
            var opts  = MakeOptions(tasksToProduce, BoundedChannelFullMode.Wait);
            var queue = CreateQueue(opts);
            var ct    = CancellationToken.None;

            // Produce many items in parallel against a queue that never blocks
            var produceTasks = Enumerable.Range(0, tasksToProduce)
                .Select(_ => Task.Run(async () =>
                {
                    await queue.QueueBackgroundWorkItemAsync(_ => Task.CompletedTask, ct);
                }, ct))
                .ToArray();

            await Task.WhenAll(produceTasks);

            // All items should have been enqueued
            Assert.Equal(tasksToProduce, queue.TotalItemsEnqueued);
        }
    }
}