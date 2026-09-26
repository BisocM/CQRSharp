using System.Threading.Channels;
using CQRSharp.Core.BackgroundTasks;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Queued dispatch (<see cref="RunMode.Queued" />) end to end: a request sent from queued work, awaited or fired and
///     forgotten, completes in a scope of its own, even with a single consumer; a caller that gives up while its request
///     waits is answered at once; a handler's failure or the queue's refusal reaches the caller; and a stream is never
///     queued. Where a queued request's context comes from is <see cref="CallerContextTests" />' subject.
/// </summary>
public sealed class QueuedDispatchTests
{
    [Fact(DisplayName = "Queued mode: a handler that sends another request completes, even with a single consumer")]
    public async Task Queued_nested_send_does_not_deadlock()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddCqrsGenerated(b => b
                .ConfigureDispatcher(o => o.RunMode = RunMode.Queued)
                .ConfigureQueue(o => o.ConsumerCount = 1)))
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var send = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new QueuedOuterCommand(), TestContext.Current.CancellationToken);

            (await send.WaitAsync(TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact(DisplayName = "Queued: a request sent from a queued handler runs at once, in a scope of its own")]
    public async Task Nested_queued_send_runs_in_its_own_scope()
    {
        using var host = QueuedHost();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var sameScope = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
                .Send(new QueuedScopeProbeQuery(), TestContext.Current.CancellationToken)
                .WaitAsync(TestContext.Current.CancellationToken);

            sameScope.Should().BeFalse("the nested request gets a scope of its own, as a queued dispatch would");
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact(DisplayName = "Queued: a request fired and forgotten from a queued handler completes after its caller's scope is gone")]
    public async Task Fire_and_forget_from_a_queued_handler_outlives_its_caller()
    {
        using var host = QueuedHost();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using (var scope = host.Services.CreateAsyncScope())
                (await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
                    .Send(new FireAndForgetOuterCommand(), TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();

            // The caller's scope is disposed; only now may the forgotten request start. That is the worst case, made
            // deterministic: on a busy thread pool the fire-and-forget task can start this late on its own.
            var signal = host.Services.GetRequiredService<FireAndForgetSignal>();
            signal.CallerGone.SetResult();
            (await signal.Done.Task.WaitAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact(DisplayName = "Queued: work queued through IBackgroundTaskManager can dispatch a queued request, even with a single consumer")]
    public async Task Manager_work_can_dispatch_a_queued_request()
    {
        using var host = QueuedHost();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var manager = host.Services.GetRequiredService<IBackgroundTaskManager>();
            var result = await manager.EnqueueAsync(async ct =>
                {
                    await using var scope = host.Services.CreateAsyncScope();
                    return await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new QueuedInnerCommand(), ct);
                }, TestContext.Current.CancellationToken)
                .WaitAsync(TestContext.Current.CancellationToken);

            result.IsSuccess.Should().BeTrue("the request runs at once instead of queueing behind the work holding the only slot");
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact(DisplayName = "Queued: a caller that gives up while its request waits is answered at once, and its handler never runs")]
    public async Task Giving_up_on_a_waiting_request_returns_at_once()
    {
        using var host = QueuedHost();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var gate = host.Services.GetRequiredService<QueueGate>();
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
            var blocker = cqrs.Send(new BlockingQueuedCommand(), TestContext.Current.CancellationToken);
            await gate.Running.Task.WaitAsync(TestContext.Current.CancellationToken);

            using var giveUp = new CancellationTokenSource();
            var waiting = cqrs.Send(new CountedQueuedCommand(), giveUp.Token);
            await giveUp.CancelAsync();

            var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            cancelled.CancellationToken.Should().Be(giveUp.Token);
            blocker.IsCompleted.Should().BeFalse("the caller was answered while the request ahead still held the only slot");

            gate.Release.SetResult();
            (await blocker.WaitAsync(TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();
            gate.Ran.Should().Be(0);
        }
        finally
        {
            gate.Release.TrySetResult();
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact(DisplayName = "Queued: a request the full queue refuses throws BackgroundTaskRejectedException to its caller")]
    public async Task A_refused_request_throws_to_its_caller()
    {
        using var host = QueuedHost(o =>
        {
            o.Capacity = 1;
            o.FullMode = BoundedChannelFullMode.DropWrite;
        });
        await host.StartAsync(TestContext.Current.CancellationToken);
        var gate = host.Services.GetRequiredService<QueueGate>();
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
            var blocker = cqrs.Send(new BlockingQueuedCommand(), TestContext.Current.CancellationToken);
            await gate.Running.Task.WaitAsync(TestContext.Current.CancellationToken);

            var queued = cqrs.Send(new CountedQueuedCommand(), TestContext.Current.CancellationToken);
            var refused = cqrs.Send(new CountedQueuedCommand(), TestContext.Current.CancellationToken);

            var rejection = await Assert.ThrowsAsync<BackgroundTaskRejectedException>(
                () => refused.WaitAsync(TestContext.Current.CancellationToken));
            rejection.Reason.Should().Be(BackgroundTaskRejectionReason.QueueFull);

            gate.Release.SetResult();
            (await queued.WaitAsync(TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();
            (await blocker.WaitAsync(TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();
            gate.Ran.Should().Be(1);
        }
        finally
        {
            gate.Release.TrySetResult();
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact(DisplayName = "Queued: a handler's exception reaches the caller of Send")]
    public async Task A_queued_handler_failure_reaches_the_caller()
    {
        using var host = QueuedHost();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var send = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
                .Send(new FailingQueuedQuery(), TestContext.Current.CancellationToken);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => send.WaitAsync(TestContext.Current.CancellationToken));
            failure.Message.Should().Be(FailingQueuedQueryHandler.Message);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Theory(DisplayName = "Queued: a stream is not queued; it runs on the flow that enumerates it, in the scope ScopeMode gives it")]
    [InlineData(ExecutionScopeMode.Current)]
    [InlineData(ExecutionScopeMode.New)]
    public async Task A_stream_runs_inline(ExecutionScopeMode scopeMode)
    {
        // Never started: no consumer runs, so a queued request would never complete. A stream that yields ran inline.
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddScoped<ScopedMarker>();
                services.AddCqrsGenerated(b => b.ConfigureDispatcher(o =>
                {
                    o.RunMode = RunMode.Queued;
                    o.ScopeMode = scopeMode;
                }));
            })
            .Build();
        await using var scope = host.Services.CreateAsyncScope();
        AmbientIdentity.Name = "enumerator";

        var seen = new List<CallerSeen>();
        await foreach (var item in scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
                           .Stream(new CallerProbeStream(), TestContext.Current.CancellationToken))
            seen.Add(item);

        var only = seen.Should().ContainSingle().Subject;
        only.HandlerAmbient.Should().Be("enumerator", "the handler runs on the enumerating flow");
        (only.ScopeId == scope.ServiceProvider.GetRequiredService<ScopedMarker>().Id).Should().Be(scopeMode == ExecutionScopeMode.Current);
    }

    private static IHost QueuedHost(Action<BackgroundTaskQueueOptions>? configureQueue = null)
        => new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<FireAndForgetSignal>();
                services.AddSingleton<QueueGate>();
                services.AddScoped<ScopedMarker>();
                services.AddCqrsGenerated(b => b
                    .ConfigureDispatcher(o => o.RunMode = RunMode.Queued)
                    .ConfigureQueue(o =>
                    {
                        o.ConsumerCount = 1;
                        configureQueue?.Invoke(o);
                    }));
            })
            .Build();
}

public sealed class QueuedOuterCommand : CommandBase;

public sealed class QueuedOuterCommandHandler(ICqrsDispatcher dispatcher) : ICommandHandler<QueuedOuterCommand>
{
    public Task<CommandResult> Handle(QueuedOuterCommand command, CancellationToken cancellationToken)
        => dispatcher.Send(new QueuedInnerCommand(), cancellationToken);
}

public sealed class QueuedScopeProbeQuery : QueryBase<bool>;

public sealed class QueuedScopeProbeQueryHandler(ICqrsDispatcher dispatcher, ScopedMarker marker) : IQueryHandler<QueuedScopeProbeQuery, bool>
{
    public async Task<bool> Handle(QueuedScopeProbeQuery query, CancellationToken cancellationToken)
        => await dispatcher.Send(new GetScopedMarkerIdQuery(), cancellationToken) == marker.Id;
}

public sealed class FireAndForgetSignal
{
    public TaskCompletionSource CallerGone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class FireAndForgetOuterCommand : CommandBase;

public sealed class FireAndForgetOuterCommandHandler(ICqrsDispatcher dispatcher, FireAndForgetSignal signal)
    : ICommandHandler<FireAndForgetOuterCommand>
{
    public Task<CommandResult> Handle(FireAndForgetOuterCommand command, CancellationToken cancellationToken)
    {
        // Fired and forgotten: the inner request starts only once the outer request and its scope are gone. A failure
        // completes the signal with it, so the test reports the error instead of timing out.
        _ = Task.Run(async () =>
        {
            try
            {
                await signal.CallerGone.Task;
                await dispatcher.Send(new FireAndForgetInnerCommand(), CancellationToken.None);
            }
            catch (Exception ex)
            {
                signal.Done.TrySetException(ex);
            }
        }, CancellationToken.None);
        return Task.FromResult(CommandResult.FromSuccess());
    }
}

public sealed class FireAndForgetInnerCommand : CommandBase;

public sealed class FireAndForgetInnerCommandHandler(FireAndForgetSignal signal, ScopedMarker marker) : ICommandHandler<FireAndForgetInnerCommand>
{
    public Task<CommandResult> Handle(FireAndForgetInnerCommand command, CancellationToken cancellationToken)
    {
        _ = marker.Id; // a disposed scope would have failed resolving this handler's dependencies
        signal.Done.TrySetResult(true);
        return Task.FromResult(CommandResult.FromSuccess());
    }
}

/// <summary>Holds the only consumer slot until a test releases it, and counts the requests that ran behind it.</summary>
public sealed class QueueGate
{
    private int _ran;

    public TaskCompletionSource Running { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Ran => Volatile.Read(ref _ran);

    public void MarkRan() => Interlocked.Increment(ref _ran);
}

public sealed class BlockingQueuedCommand : CommandBase;

public sealed class BlockingQueuedCommandHandler(QueueGate gate) : ICommandHandler<BlockingQueuedCommand>
{
    public async Task<CommandResult> Handle(BlockingQueuedCommand command, CancellationToken cancellationToken)
    {
        gate.Running.TrySetResult();
        await gate.Release.Task;
        return CommandResult.FromSuccess();
    }
}

public sealed class CountedQueuedCommand : CommandBase;

public sealed class CountedQueuedCommandHandler(QueueGate gate) : ICommandHandler<CountedQueuedCommand>
{
    public Task<CommandResult> Handle(CountedQueuedCommand command, CancellationToken cancellationToken)
    {
        gate.MarkRan();
        return Task.FromResult(CommandResult.FromSuccess());
    }
}

public sealed class FailingQueuedQuery : QueryBase<int>;

public sealed class FailingQueuedQueryHandler : IQueryHandler<FailingQueuedQuery, int>
{
    public const string Message = "The queued handler failed.";

    public Task<int> Handle(FailingQueuedQuery query, CancellationToken cancellationToken)
        => throw new InvalidOperationException(Message);
}
