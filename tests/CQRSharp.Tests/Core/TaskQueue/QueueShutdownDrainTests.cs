using System.Threading.Channels;
using CQRSharp.Core.Background.TaskQueue;
using CQRSharp.Pipelines;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CQRSharp.Tests.Core.TaskQueue;

/// <summary>
///     What happens to work that is still <em>queued</em> — not yet executing — when the host stops. With one consumer
///     slot held by a gated item, everything enqueued behind it is backlog at the moment shutdown begins.
/// </summary>
public sealed class QueueShutdownDrainTests
{
    [Fact(DisplayName = "Shutdown runs the queued backlog before the consumer stops")]
    public async Task Queued_work_is_drained()
    {
        var (queue, consumer) = Create(new BackgroundTaskQueueOptions { Capacity = 16, ConsumerCount = 1, ShutdownTimeout = TimeSpan.FromSeconds(10) });
        await consumer.StartAsync(CancellationToken.None);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = queue.EnqueueAsync(async _ =>
        {
            running.SetResult();
            await gate.Task;
        });
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var ran = 0;
        var backlog = Enumerable.Range(0, 5)
            .Select(_ => queue.EnqueueAsync(_ =>
            {
                Interlocked.Increment(ref ran);
                return Task.CompletedTask;
            }))
            .ToArray();

        var stopping = consumer.StopAsync(CancellationToken.None);
        gate.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));

        await first;
        await Task.WhenAll(backlog);
        ran.Should().Be(5);

        queue.Dispose();
    }

    [Fact(DisplayName = "Shutdown refuses new work from the first moment")]
    public async Task New_work_is_refused_once_stopping()
    {
        var (queue, consumer) = Create(new BackgroundTaskQueueOptions { Capacity = 16, ConsumerCount = 1, ShutdownTimeout = TimeSpan.FromSeconds(10) });
        await consumer.StartAsync(CancellationToken.None);
        await consumer.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        var late = () => queue.EnqueueAsync(_ => Task.CompletedTask);
        await late.Should().ThrowAsync<ChannelClosedException>();

        queue.Dispose();
    }

    [Fact(DisplayName = "Backlog the shutdown budget does not cover is cancelled, so its callers do not hang")]
    public async Task Backlog_past_the_deadline_is_cancelled()
    {
        var (queue, consumer) = Create(new BackgroundTaskQueueOptions { Capacity = 16, ConsumerCount = 1, ShutdownTimeout = TimeSpan.FromMilliseconds(200) });
        await consumer.StartAsync(CancellationToken.None);

        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = queue.EnqueueAsync(async ct =>
        {
            running.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var ran = false;
        var queued = queue.EnqueueAsync(_ =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        await consumer.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        await ((Func<Task>)(() => first)).Should().ThrowAsync<OperationCanceledException>("in-flight work is cancelled once the budget is spent");
        await ((Func<Task>)(() => queued)).Should().ThrowAsync<OperationCanceledException>("queued work that never ran is cancelled, not left pending");
        ran.Should().BeFalse();

        queue.Dispose();
    }

    [Fact(DisplayName = "DrainOnShutdown = false cancels the backlog but still lets in-flight work finish")]
    public async Task Drain_can_be_turned_off()
    {
        var (queue, consumer) = Create(new BackgroundTaskQueueOptions
        {
            Capacity = 16, ConsumerCount = 1, ShutdownTimeout = TimeSpan.FromSeconds(10), DrainOnShutdown = false
        });
        await consumer.StartAsync(CancellationToken.None);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = queue.EnqueueAsync(async _ =>
        {
            running.SetResult();
            await gate.Task;
        });
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var ran = false;
        var queued = queue.EnqueueAsync(_ =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        var stopping = consumer.StopAsync(CancellationToken.None);
        gate.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));

        await first;
        await ((Func<Task>)(() => queued)).Should().ThrowAsync<OperationCanceledException>();
        ran.Should().BeFalse();

        queue.Dispose();
    }

    private static (BackgroundTaskQueue Queue, BackgroundTaskQueueConsumer Consumer) Create(BackgroundTaskQueueOptions options)
    {
        options.FullMode = BoundedChannelFullMode.Wait;
        options.EnableMetrics = false;
        var wrapped = Options.Create(options);
        var queue = new BackgroundTaskQueue(
            wrapped,
            new SingleDispatcherScopeFactory(new ControllableDispatcher()),
            new TestMetricsReporter(),
            NullLogger<BackgroundTaskQueue>.Instance,
            new TestHostApplicationLifetime());
        var consumer = new BackgroundTaskQueueConsumer(queue, NullLogger<BackgroundTaskQueueConsumer>.Instance, wrapped, new ConsumerReadiness());
        return (queue, consumer);
    }
}
