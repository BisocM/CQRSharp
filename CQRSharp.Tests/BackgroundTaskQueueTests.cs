using CQRSharp.Core.BackgroundTasks;
using Microsoft.Extensions.Logging;
using Moq;

namespace CQRSharp.Tests;

public class BackgroundTaskQueueTests
{
    [Fact]
    public async Task QueueBackgroundWorkItemAsync_EnqueuesWorkItemSuccessfully()
    {
        // Arrange
        var queue = new BackgroundTaskQueue();
        var cts = new CancellationTokenSource();

        // Act
        await queue.QueueBackgroundWorkItemAsync(async token => { await Task.Delay(10, token); }, cts.Token);

        // Assert
        var dequeued = await queue.DequeueAsync(cts.Token);
        Assert.NotNull(dequeued);
    }

    [Fact]
    public async Task QueueBackgroundWorkItemAsync_ThrowsIfWorkItemIsNull()
    {
        // Arrange
        var queue = new BackgroundTaskQueue();
        var cts = new CancellationTokenSource();

        // Act / Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            queue.QueueBackgroundWorkItemAsync(null!, cts.Token));
    }

    [Fact]
    public async Task DequeueAsync_CancelsWhenTokenIsCancelled()
    {
        // Arrange
        var queue = new BackgroundTaskQueue();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act / Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            var _ = await queue.DequeueAsync(cts.Token);
        });
    }

    [Fact]
    public async Task DequeueAsync_ReturnsWorkItemsInFIFOOrder()
    {
        // Arrange
        var queue = new BackgroundTaskQueue();
        var cts = new CancellationTokenSource();
        await queue.QueueBackgroundWorkItemAsync(ct => Task.FromResult(1), cts.Token);
        await queue.QueueBackgroundWorkItemAsync(ct => Task.FromResult(2), cts.Token);

        // Act
        var first = await queue.DequeueAsync(cts.Token);
        var second = await queue.DequeueAsync(cts.Token);

        // Assert
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }
}

public class BackgroundTaskServiceTests
{
    [Fact]
    public async Task BackgroundTaskService_ProcessesQueuedWorkItems()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<BackgroundTaskService>>();
        var queue = new BackgroundTaskQueue();
        var service = new BackgroundTaskService(queue, loggerMock.Object);

        // We'll run the BackgroundTaskService in a separate token to simulate host run.
        var cts = new CancellationTokenSource();
        var executeTask = service.StartAsync(cts.Token);

        int counter = 0;
        await queue.QueueBackgroundWorkItemAsync(async token => { Interlocked.Increment(ref counter); }, cts.Token);
        await Task.Delay(500); // Give some time for it to process.

        // Assert
        Assert.Equal(1, Volatile.Read(ref counter));

        // Cleanup
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task BackgroundTaskService_LogsAndContinuesOnWorkItemException()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<BackgroundTaskService>>();
        var queue = new BackgroundTaskQueue();
        var service = new BackgroundTaskService(queue, loggerMock.Object);

        var cts = new CancellationTokenSource();
        var executeTask = service.StartAsync(cts.Token);

        // Queue an item that throws
        await queue.QueueBackgroundWorkItemAsync(token => throw new InvalidOperationException("Test exception"), cts.Token);
        await Task.Delay(300);

        // The service should not crash. We can check logger was called with an error.
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString().Contains("Error occurred executing background work item")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task BackgroundTaskService_StopsGracefully()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<BackgroundTaskService>>();
        var queue = new BackgroundTaskQueue();
        var service = new BackgroundTaskService(queue, loggerMock.Object);

        var cts = new CancellationTokenSource();
        var executeTask = service.StartAsync(cts.Token);

        // Act
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        // Assert
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString().Contains("Background Task Service is stopping.")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}