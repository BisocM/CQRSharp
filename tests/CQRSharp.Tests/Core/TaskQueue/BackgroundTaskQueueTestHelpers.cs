// CQRSharp.Tests/Core/TaskQueue/BackgroundTaskQueueTestHelpers.cs

using System.Collections.Concurrent;
using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Core.Background.TaskQueue.Telemetry;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using static System.Threading.Tasks.Task;

namespace CQRSharp.Tests.Core.TaskQueue;

/// <summary>
///     A mock implementation of <see cref="IDirectNotificationDispatcher" /> that allows controlling success and failure for testing purposes.
/// </summary>
public class ControllableDispatcher : IDirectNotificationDispatcher
{
    private readonly int _failCount;
    private readonly List<INotification> _published = new();

    // Completed once a notification is published successfully (past any configured failures), so tests can await the
    // actual asynchronous dispatch instead of sleeping a fixed interval and racing the background pump under load.
    private readonly TaskCompletionSource _publishedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _currentCallCount;

    public ControllableDispatcher(int failCount = 0)
    {
        _failCount = failCount;
    }

    public int CallCount => _currentCallCount;

    /// <summary>Completes once a notification has been published successfully (past any configured failures).</summary>
    public Task Published => _publishedSignal.Task;

    /// <summary>A thread-safe snapshot of the notifications published so far.</summary>
    public IReadOnlyList<INotification> Snapshot()
    {
        lock (_published)
        {
            return new List<INotification>(_published);
        }
    }

    public Task Publish(INotification notification, CancellationToken token = default) =>
        Publish<INotification>(notification, token);

    public Task Publish<TNotification>(TNotification notification, CancellationToken token) where TNotification : INotification
    {
        Interlocked.Increment(ref _currentCallCount);

        if (_currentCallCount <= _failCount) throw new InvalidOperationException("Dispatcher configured to fail.");

        lock (_published)
        {
            _published.Add(notification);
        }

        _publishedSignal.TrySetResult();
        return CompletedTask;
    }
}

/// <summary>
///     A thread-safe mock implementation of <see cref="IQueueMetricsReporter" /> for capturing queue metrics during tests.
/// </summary>
public class TestMetricsReporter : IQueueMetricsReporter
{
    private readonly ConcurrentBag<TimeSpan> _latencies = new();
    private long _dequeuedCount;
    private long _droppedNewestCount;
    private long _droppedOldestCount;
    private long _enqueuedCount;
    public long EnqueuedCount => Interlocked.Read(ref _enqueuedCount);
    public long DequeuedCount => Interlocked.Read(ref _dequeuedCount);
    public long DroppedNewestCount => Interlocked.Read(ref _droppedNewestCount);
    public long DroppedOldestCount => Interlocked.Read(ref _droppedOldestCount);
    public IReadOnlyCollection<TimeSpan> Latencies => _latencies;

    public long CurrentCount => Interlocked.Read(ref _enqueuedCount) - Interlocked.Read(ref _dequeuedCount);

    public void ItemEnqueued() => Interlocked.Increment(ref _enqueuedCount);
    public void ItemDequeued() => Interlocked.Increment(ref _dequeuedCount);
    public void ItemDroppedNewest() => Interlocked.Increment(ref _droppedNewestCount);
    public void ItemDroppedOldest() => Interlocked.Increment(ref _droppedOldestCount);
    public void RecordLatency(TimeSpan latency) => _latencies.Add(latency);

    public void Dispose()
    {
    }
}

/// <summary>
///     A helper class to provide <see cref="IOptions{BackgroundTaskQueueOptions}" /> in tests.
/// </summary>
public class TestOptions(BackgroundTaskQueueOptions options) : IOptions<BackgroundTaskQueueOptions>
{
    public BackgroundTaskQueueOptions Value { get; } = options;
}

/// <summary>
///     A mock implementation of <see cref="IHostApplicationLifetime" /> for controlling application lifetime events in tests.
/// </summary>
public class TestHostApplicationLifetime : IHostApplicationLifetime
{
    private readonly CancellationTokenSource _stoppingSource = new();
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => _stoppingSource.Token;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() => _stoppingSource.Cancel();
}

/// <summary>
///     A minimal <see cref="IServiceScopeFactory" /> that always resolves a single
///     <see cref="IDirectNotificationDispatcher" /> instance within created scopes.
/// </summary>
public sealed class SingleDispatcherScopeFactory(IDirectNotificationDispatcher dispatcher) : IServiceScopeFactory
{
    private readonly IDirectNotificationDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public IServiceScope CreateScope() => new SingleDispatcherScope(_dispatcher);

    private sealed class SingleDispatcherScope(IDirectNotificationDispatcher dispatcher) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new SingleDispatcherServiceProvider(dispatcher);

        public void Dispose()
        {
        }
    }

    private sealed class SingleDispatcherServiceProvider(IDirectNotificationDispatcher dispatcher) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            return serviceType == typeof(IDirectNotificationDispatcher) ? dispatcher : null;
        }
    }
}