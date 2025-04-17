using System.Collections.Concurrent;
using System.Threading.Channels;
using CQRSharp.Core.BackgroundTasks;
using CQRSharp.Core.BackgroundTasks.Types;
using CQRSharp.Core.Options;
using Microsoft.Extensions.Options;

namespace CQRSharp.Tests
{
    public class BackgroundTaskQueueTests
    {
        private class TestOptions(BackgroundTaskQueueOptions options) : IOptions<BackgroundTaskQueueOptions>
        {
            public BackgroundTaskQueueOptions Value { get; } = options ?? throw new ArgumentNullException(nameof(options));
        }

        private static BackgroundTaskQueueOptions MakeOptions(
            int capacity,
            BoundedChannelFullMode fullMode,
            Action<TaskRejectedEventArgs>? onRejected = null)
            => new()
            {
                Capacity = capacity,
                FullMode = fullMode,
                ConsumerCount = 1,
                DequeueBatchSize = 1,
                OnTaskRejected = onRejected
            };

        private static IOptions<BackgroundTaskQueueOptions> Opts(BackgroundTaskQueueOptions o)
            => new TestOptions(o);

        [Fact(DisplayName = "Unbounded queue preserves FIFO order and never drops items")]
        public async Task UnboundedQueue_PreservesOrderAndNoDrops()
        {
            var opts = MakeOptions(capacity: 0, fullMode: BoundedChannelFullMode.Wait);
            var queue = new BackgroundTaskQueue(Opts(opts));
            var ct = CancellationToken.None;
            var items = Enumerable.Range(1, 50)
                                  .Select(i => (Func<CancellationToken, Task>)(_ => Task.CompletedTask))
                                  .ToList();

            foreach (var w in items)
                await queue.QueueBackgroundWorkItemAsync(w, ct);

            var drained = new List<Func<CancellationToken, Task>>();
            for (int i = 0; i < items.Count; i++)
            {
                var queued = await queue.DequeueAsync(ct);
                drained.Add(queued.WorkItem);
            }

            Assert.Equal(items.Count, drained.Count);
            Assert.True(
                drained.SequenceEqual(items),
                "Unbounded queue should return items in the exact order enqueued."
            );
        }

        [Fact(DisplayName = "DropNewest policy rejects newest items once full")]
        public async Task DropNewest_RejectsNewestBeyondCapacity()
        {
            var rejected = new List<TaskRejectedEventArgs>();
            var opts = MakeOptions(
                capacity: 2,
                fullMode: BoundedChannelFullMode.DropNewest,
                onRejected: rejected.Add
            );
            var queue = new BackgroundTaskQueue(Opts(opts));
            var ct = CancellationToken.None;

            await queue.QueueBackgroundWorkItemAsync(_ => Task.CompletedTask, ct);
            await queue.QueueBackgroundWorkItemAsync(_ => Task.CompletedTask, ct);

            var third = (Func<CancellationToken, Task>)(_ => Task.CompletedTask);
            await queue.QueueBackgroundWorkItemAsync(third, ct);

            var got = new[]
            {
                (await queue.DequeueAsync(ct)).WorkItem,
                (await queue.DequeueAsync(ct)).WorkItem
            };

            Assert.Single(rejected);
            Assert.DoesNotContain(third, got);
            Assert.Equal(2, got.Length);
        }

        [Fact(DisplayName = "DropWrite policy behaves like DropNewest")]
        public async Task DropWrite_RejectsWriteWhenFull()
        {
            var rejected = new List<TaskRejectedEventArgs>();
            var opts = MakeOptions(
                capacity: 2,
                fullMode: BoundedChannelFullMode.DropWrite,
                onRejected: rejected.Add
            );
            var queue = new BackgroundTaskQueue(Opts(opts));
            var ct = CancellationToken.None;

            await queue.QueueBackgroundWorkItemAsync(_ => Task.CompletedTask, ct);
            await queue.QueueBackgroundWorkItemAsync(_ => Task.CompletedTask, ct);

            var third = (Func<CancellationToken, Task>)(_ => Task.CompletedTask);
            await queue.QueueBackgroundWorkItemAsync(third, ct);

            var got = new[]
            {
                (await queue.DequeueAsync(ct)).WorkItem,
                (await queue.DequeueAsync(ct)).WorkItem
            };

            Assert.Single(rejected);
            Assert.DoesNotContain(third, got);
        }

        [Fact(DisplayName = "DropOldest policy replaces oldest when full")]
        public async Task DropOldest_ReplacesOldestWhenFull()
        {
            var rejected = new List<TaskRejectedEventArgs>();
            var opts = MakeOptions(
                capacity: 2,
                fullMode: BoundedChannelFullMode.DropOldest,
                onRejected: rejected.Add
            );
            var queue = new BackgroundTaskQueue(Opts(opts));
            var ct = CancellationToken.None;

            var first = (Func<CancellationToken, Task>)(_ => Task.CompletedTask);
            var second = (Func<CancellationToken, Task>)(_ => Task.CompletedTask);
            var third = (Func<CancellationToken, Task>)(_ => Task.CompletedTask);

            await queue.QueueBackgroundWorkItemAsync(first, ct);
            await queue.QueueBackgroundWorkItemAsync(second, ct);
            await queue.QueueBackgroundWorkItemAsync(third, ct);

            var got = new[]
            {
                (await queue.DequeueAsync(ct)).WorkItem,
                (await queue.DequeueAsync(ct)).WorkItem
            };

            Assert.Single(rejected);
            Assert.Equal(new[] { second, third }, got);
        }

        [Fact(DisplayName = "Wait policy blocks and throws when canceled")]
        public async Task WaitMode_CancelsWhenCancelledBeforeSpaceAvailable()
        {
            var opts = MakeOptions(capacity: 1, fullMode: BoundedChannelFullMode.Wait);
            var queue = new BackgroundTaskQueue(Opts(opts));

            var cts = new CancellationTokenSource();
            var ct = cts.Token;

            //Fill once
            await queue.QueueBackgroundWorkItemAsync(_ => Task.CompletedTask, ct);

            //Cancel before second enqueue
            cts.Cancel();

            await Assert.ThrowsAsync<TaskCanceledException>(
                () => queue.QueueBackgroundWorkItemAsync(_ => Task.CompletedTask, ct)
            );
        }

        [Fact(DisplayName = "DequeueAsync throws when canceled immediately")]
        public async Task DequeueAsync_RespectsImmediateCancellation()
        {
            var opts = MakeOptions(capacity: 1, fullMode: BoundedChannelFullMode.Wait);
            var queue = new BackgroundTaskQueue(Opts(opts));
            var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<TaskCanceledException>(
                () => queue.DequeueAsync(cts.Token).AsTask()
            );
        }

        [Fact(DisplayName = "Stress test under DropNewest with high concurrency and capacity reporting")]
        public async Task StressTest_HighConcurrencyDropNewest()
        {
            const int capacity = 100;
            const int producerCount = 1000;
            var rejected = new ConcurrentBag<TaskRejectedEventArgs>();
            var opts = MakeOptions(capacity, BoundedChannelFullMode.DropNewest, onRejected: rejected.Add);
            var queue = new BackgroundTaskQueue(Opts(opts));
            var ct = CancellationToken.None;

            //Fill to capacity with long-running work items
            for (int i = 0; i < capacity; i++)
                await queue.QueueBackgroundWorkItemAsync(_ => Task.Delay(TimeSpan.FromSeconds(10)), ct);

            //Queue should be at capacity
            Assert.Equal(capacity, queue.Reader.Count);

            //Act: spawn many producers in parallel
            var tasks = Enumerable.Range(0, producerCount)
                .Select(_ => Task.Run(() => queue.QueueBackgroundWorkItemAsync(_ => Task.CompletedTask, ct), ct))
                .ToArray();
            await Task.WhenAll(tasks);

            //Assert: still at capacity and all extras rejected
            Assert.Equal(capacity, queue.Reader.Count);
            Assert.Equal(producerCount, rejected.Count);
        }

        [Fact(DisplayName = "Stress test on unbounded queue under high concurrency")]
        public async Task StressTest_UnboundedHighConcurrency()
        {
            const int tasksToProduce = 100000;
            var opts = MakeOptions(capacity: 0, fullMode: BoundedChannelFullMode.Wait);
            var queue = new BackgroundTaskQueue(Opts(opts));
            var ct = CancellationToken.None;

            //Act: produce many items in parallel
            var produceTasks = Enumerable.Range(0, tasksToProduce)
                .Select(_ => Task.Run(() => queue.QueueBackgroundWorkItemAsync(_ => Task.CompletedTask, ct), ct))
                .ToArray();
            await Task.WhenAll(produceTasks);

            //Assert: unbounded queue holds all
            Assert.Equal(tasksToProduce, queue.Reader.Count);
        }
    }
}