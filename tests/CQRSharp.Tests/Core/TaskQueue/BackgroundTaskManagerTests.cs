using System.Collections;
using System.Collections.Concurrent;
using System.Threading.Channels;
using CQRSharp.Core.Background.TaskQueue;
using CQRSharp.Core.Background.TaskQueue.Types;
using CQRSharp.Core.Notifications.Types;
using CQRSharp.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;
using static System.Threading.Tasks.Task;

namespace CQRSharp.Tests.Core.TaskQueue;

/// <summary>
///     Contains unit and integration tests for the <see cref="BackgroundTaskQueue" /> and its related components.
/// </summary>
public class BackgroundTaskManagerTests
{
    private readonly ITestOutputHelper _testOutputHelper;

    public BackgroundTaskManagerTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    private static BackgroundTaskQueueOptions MakeOptions(int capacity, BoundedChannelFullMode fullMode) => new()
    {
        Capacity = capacity,
        FullMode = fullMode,
        ConsumerCount = 1,
        EnableMetrics = true,
        NotificationMaxRetries = 3,
        NotificationRetryDelay = TimeSpan.FromMilliseconds(5),
        ShutdownTimeout = TimeSpan.FromSeconds(5)
    };

    private static IOptions<BackgroundTaskQueueOptions> Opts(BackgroundTaskQueueOptions o) => new TestOptions(o);

    private (BackgroundTaskQueue Queue, TestMetricsReporter Metrics, ControllableDispatcher Dispatcher) CreateTestSystem(
        BackgroundTaskQueueOptions opts, ILogger<BackgroundTaskQueue>? logger = null)
    {
        var dispatcher = new ControllableDispatcher();
        var scopeFactory = new SingleDispatcherScopeFactory(dispatcher);
        var lifetime = new TestHostApplicationLifetime();
        var metrics = new TestMetricsReporter();
        var queueLogger = logger ?? NullLogger<BackgroundTaskQueue>.Instance;
        var queue = new BackgroundTaskQueue(Opts(opts), scopeFactory, metrics, queueLogger, lifetime);
        return (queue, metrics, dispatcher);
    }

    [Fact(DisplayName = "Unit: Bounded queue preserves FIFO order and never drops items")]
    public async Task BoundedQueue_PreservesOrderAndNoDrops()
    {
        // Arrange
        const int itemCount = 50;
        var opts = MakeOptions(itemCount, BoundedChannelFullMode.Wait);
        var (queue, _, _) = CreateTestSystem(opts);
        var items = Enumerable.Range(1, itemCount).Select(_ => (Func<CancellationToken, Task>)(_ => CompletedTask)).ToList();

        // Act
        foreach (var workItem in items)
            await queue.QueueBackgroundWorkItemAsync(workItem, CancellationToken.None);

        var drained = new List<Func<CancellationToken, Task>>();
        var taskQueue = (IBackgroundTaskQueue)queue;
        for (var i = 0; i < items.Count; i++)
        {
            var queued = await taskQueue.DequeueAsync(CancellationToken.None);
            drained.Add(queued.WorkItem);
        }

        // Assert
        Assert.Equal(items.Count, drained.Count);
        Assert.True(drained.SequenceEqual(items), "Bounded queue should return items in FIFO order.");
    }

    [Fact(DisplayName = "Unit: DropNewest policy evicts the newest existing item when full")]
    public async Task DropNewest_EvictsNewestWhenFull()
    {
        // Arrange
        var opts = MakeOptions(2, BoundedChannelFullMode.DropNewest);
        var (queue, metrics, _) = CreateTestSystem(opts);
        var itemA = (Func<CancellationToken, Task>)(_ => CompletedTask);
        var itemB = (Func<CancellationToken, Task>)(_ => CompletedTask);
        var itemC = (Func<CancellationToken, Task>)(_ => CompletedTask);

        // Act
        await queue.QueueBackgroundWorkItemAsync(itemA, CancellationToken.None); // In queue
        await queue.QueueBackgroundWorkItemAsync(itemB, CancellationToken.None); // In queue, will be dropped
        var resultC = await queue.QueueBackgroundWorkItemAsync(itemC, CancellationToken.None); // In queue, displaces B

        var taskQueue = (IBackgroundTaskQueue)queue;
        var drainedItems = new[]
        {
            (await taskQueue.DequeueAsync(CancellationToken.None)).WorkItem,
            (await taskQueue.DequeueAsync(CancellationToken.None)).WorkItem
        };

        // Assert
        Assert.Equal(QueueWriteResultCode.Enqueued, resultC.Result);
        Assert.Equal(3, metrics.EnqueuedCount);
        Assert.Equal(1, metrics.DroppedNewestCount);
        Assert.Contains(itemA, drainedItems);
        Assert.Contains(itemC, drainedItems);
        Assert.DoesNotContain(itemB, drainedItems);
    }

    [Fact(DisplayName = "Unit: DropWrite policy drops incoming item when full")]
    public async Task DropWrite_DropsIncomingWhenFull()
    {
        // Arrange
        var logger = LoggerFactory.Create(builder => builder.AddProvider(new XunitLoggerProvider(_testOutputHelper))).CreateLogger<BackgroundTaskQueue>();
        var opts = MakeOptions(2, BoundedChannelFullMode.DropWrite);
        var (queue, metrics, _) = CreateTestSystem(opts, logger);
        var itemA = (Func<CancellationToken, Task>)(_ => Delay(1, CancellationToken.None));
        var itemB = (Func<CancellationToken, Task>)(_ => Delay(1, CancellationToken.None));
        var itemC = (Func<CancellationToken, Task>)(_ => Delay(1, CancellationToken.None));

        // Act
        await queue.QueueBackgroundWorkItemAsync(itemA, CancellationToken.None);
        await queue.QueueBackgroundWorkItemAsync(itemB, CancellationToken.None);
        var resultC = await queue.QueueBackgroundWorkItemAsync(itemC, CancellationToken.None); // This should be dropped

        // Assert
        Assert.Equal(QueueWriteResultCode.DroppedNewest, resultC.Result);
        Assert.Equal(2, metrics.EnqueuedCount);
        Assert.Equal(1, metrics.DroppedNewestCount);

        var taskQueue = (IBackgroundTaskQueue)queue;
        var drainedItems = new[]
        {
            (await taskQueue.DequeueAsync(CancellationToken.None)).WorkItem,
            (await taskQueue.DequeueAsync(CancellationToken.None)).WorkItem
        };
        Assert.Contains(itemA, drainedItems);
        Assert.Contains(itemB, drainedItems);
        Assert.DoesNotContain(itemC, drainedItems);
    }

    [Fact(DisplayName = "Unit: DropWrite causes EnqueueAsync to fault (no hangs)")]
    public async Task DropWrite_EnqueueAsyncFaultsWithoutHanging()
    {
        // Arrange
        var opts = MakeOptions(1, BoundedChannelFullMode.DropWrite);
        var (queue, _, _) = CreateTestSystem(opts);
        await queue.QueueBackgroundWorkItemAsync(_ => CompletedTask, CancellationToken.None); // Fill capacity

        // Act
        var rejected = queue.EnqueueAsync(_ => CompletedTask);

        // Assert
        await Assert.ThrowsAsync<ChannelClosedException>(() => rejected.WaitAsync(TimeSpan.FromSeconds(1)));
        queue.Dispose();
    }

    [Fact(DisplayName = "Unit: DropNewest eviction completes dropped awaiters (no hangs)")]
    public async Task DropNewest_EvictionCompletesAwaitersWithoutHanging()
    {
        // Arrange
        var opts = MakeOptions(1, BoundedChannelFullMode.DropNewest);
        var (queue, _, _) = CreateTestSystem(opts);

        var droppedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = await queue.QueueBackgroundWorkItemAsync(
            _ => CompletedTask,
            CancellationToken.None,
            ex => droppedTcs.TrySetException(ex),
            token => droppedTcs.TrySetCanceled(token));
        Assert.Equal(QueueWriteResultCode.Enqueued, first.Result);

        // Act - Evict the first item.
        await queue.QueueBackgroundWorkItemAsync(_ => CompletedTask, CancellationToken.None);

        // Assert - The dropped awaiter completes quickly.
        await Assert.ThrowsAsync<ChannelClosedException>(() => droppedTcs.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        queue.Dispose();
    }

    [Fact(DisplayName = "Unit: DropOldest policy replaces oldest when full")]
    public async Task DropOldest_ReplacesOldestWhenFull()
    {
        // Arrange
        var opts = MakeOptions(2, BoundedChannelFullMode.DropOldest);
        var (queue, metrics, _) = CreateTestSystem(opts);
        var first = (Func<CancellationToken, Task>)(_ => CompletedTask);
        var second = (Func<CancellationToken, Task>)(_ => CompletedTask);
        var third = (Func<CancellationToken, Task>)(_ => CompletedTask);

        // Act
        await queue.QueueBackgroundWorkItemAsync(first, CancellationToken.None);
        await queue.QueueBackgroundWorkItemAsync(second, CancellationToken.None);
        var result = await queue.QueueBackgroundWorkItemAsync(third, CancellationToken.None); // Displaces 'first'

        // Assert
        Assert.Equal(QueueWriteResultCode.Enqueued, result.Result);
        Assert.Equal(1, metrics.DroppedOldestCount);
        Assert.Equal(3, metrics.EnqueuedCount);

        var taskQueue = (IBackgroundTaskQueue)queue;
        var drainedItems = new[]
        {
            (await taskQueue.DequeueAsync(CancellationToken.None)).WorkItem,
            (await taskQueue.DequeueAsync(CancellationToken.None)).WorkItem
        };
        Assert.Equal([second, third], drainedItems);
    }

    [Fact(DisplayName = "Unit: Wait-mode enqueue cancellation completes (no hangs)")]
    public async Task Wait_EnqueueCancellationCompletesWithoutHanging()
    {
        // Arrange
        var opts = MakeOptions(1, BoundedChannelFullMode.Wait);
        var (queue, _, _) = CreateTestSystem(opts);
        await queue.QueueBackgroundWorkItemAsync(_ => CompletedTask, CancellationToken.None); // Fill capacity

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Act
        var pending = queue.EnqueueAsync(_ => CompletedTask, cts.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(1)));
        queue.Dispose();
    }

    [Fact(DisplayName = "Integration: Wait policy is thread-safe with multiple producers and a real consumer")]
    public async Task Wait_IsThreadSafe_WithRealConsumer()
    {
        // Arrange
        const int itemsPerProducer = 100;
        const int producerCount = 5;
        const int consumerCount = 3;
        const int totalItems = itemsPerProducer * producerCount;

        var opts = MakeOptions(50, BoundedChannelFullMode.Wait);
        opts.ConsumerCount = consumerCount;

        var (queue, metrics, _) = CreateTestSystem(opts);
        var itemsProcessed = new ConcurrentBag<int>();

        var consumer = new BackgroundTaskQueueConsumer(queue, NullLogger<BackgroundTaskQueueConsumer>.Instance, Opts(opts));
        var consumerTask = consumer.StartAsync(CancellationToken.None);

        // Act
        var producerTasks = Enumerable.Range(0, producerCount).Select(p => Run(async () =>
        {
            for (var i = 0; i < itemsPerProducer; i++)
            {
                var itemValue = p * itemsPerProducer + i;
                await queue.EnqueueAsync(ct =>
                {
                    itemsProcessed.Add(itemValue);
                    return CompletedTask;
                });
            }
        })).ToList();

        await WhenAll(producerTasks);
        await consumer.StopAsync(CancellationToken.None);
        await consumerTask;
        queue.Dispose();

        // Assert
        Assert.Equal(totalItems, itemsProcessed.Count);
        Assert.Equal(totalItems, metrics.EnqueuedCount);
        Assert.Equal(totalItems, metrics.DequeuedCount);
    }

    [Fact(DisplayName = "Integration: EnqueueAsync<T> should return correct result via consumer")]
    public async Task EnqueueAsync_WithResult_ReturnsCorrectValue()
    {
        // Arrange
        var opts = MakeOptions(1, BoundedChannelFullMode.Wait);
        var (queue, _, _) = CreateTestSystem(opts);
        var consumer = new BackgroundTaskQueueConsumer(queue, NullLogger<BackgroundTaskQueueConsumer>.Instance, Opts(opts));
        var consumerTask = consumer.StartAsync(CancellationToken.None);

        // Act
        var resultTask = queue.EnqueueAsync(ct => FromResult(42));
        var finalResult = await resultTask;

        // Clean up
        await consumer.StopAsync(CancellationToken.None);
        await consumerTask;
        queue.Dispose();

        // Assert
        Assert.Equal(42, finalResult);
    }

    [Fact(DisplayName = "Enqueue should publish a TaskEnqueuedNotification")]
    public async Task Enqueue_PublishesTaskEnqueuedNotification()
    {
        // Arrange
        var opts = MakeOptions(5, BoundedChannelFullMode.Wait);
        var (queue, _, dispatcher) = CreateTestSystem(opts);

        // Act
        await queue.QueueBackgroundWorkItemAsync(_ => CompletedTask, CancellationToken.None);
        await Delay(100); // Allow time for notification to be processed

        // Assert
        var notification = Assert.Single((IEnumerable)dispatcher.PublishedNotifications);
        Assert.IsType<TaskEnqueuedNotification>(notification);
    }

    [Fact(DisplayName = "Notification dispatch should retry on failure and then succeed")]
    public async Task NotificationDispatch_RetriesOnFailureAndThenSucceeds()
    {
        // Arrange
        var opts = MakeOptions(5, BoundedChannelFullMode.Wait);
        opts.NotificationMaxRetries = 5;
        var dispatcher = new ControllableDispatcher(2); // Fails twice, succeeds on the 3rd try
        var scopeFactory = new SingleDispatcherScopeFactory(dispatcher);
        var queue = new BackgroundTaskQueue(
            Opts(opts),
            scopeFactory,
            new TestMetricsReporter(),
            NullLogger<BackgroundTaskQueue>.Instance,
            new TestHostApplicationLifetime());

        // Act
        await queue.QueueBackgroundWorkItemAsync(_ => CompletedTask, CancellationToken.None);
        await Delay(100); // Wait for retries

        // Assert
        Assert.Equal(3, dispatcher.CallCount); // 2 failures + 1 success
        Assert.Single((IEnumerable)dispatcher.PublishedNotifications);
        queue.Dispose();
    }

    [Fact(DisplayName = "Dispose should complete the channel and return drop result on new writes")]
    public async Task Dispose_CompletesChannelAndReturnsDropResultOnNewWrites()
    {
        // Arrange
        var opts = MakeOptions(5, BoundedChannelFullMode.Wait);
        var (queue, _, _) = CreateTestSystem(opts);

        // Act
        queue.Dispose();
        var result = await queue.QueueBackgroundWorkItemAsync(_ => CompletedTask, CancellationToken.None);

        // Assert
        Assert.Equal(QueueWriteResultCode.DroppedNewest, result.Result);
    }
}