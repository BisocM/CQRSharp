using System.Collections.Concurrent;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Hot-path contention evidence for the core headliners — command/query dispatch, the behavior pipeline, and the
///     notification system — exercised through the real source-generated wiring (<c>AddCqrsGenerated</c>) under heavy
///     parallelism and in both run modes. Each test asserts a deterministic, observable invariant rather than just "no
///     exception", so a concurrency defect would show up as a hard failure rather than a flaky pass.
/// </summary>
public sealed class HotPathContentionStressTests
{
    private const int Fanout = 64;
    private const int Iterations = 64;

    // 1) Concurrent command + query dispatch must never cross-talk: every distinct request gets its own correct
    //    result, and no command dispatch is lost. Run for both scope modes (shared current scope vs a new scope per
    //    dispatch) since both share the same stateless executor/registries/dispatchers.
    [Theory(DisplayName = "Concurrent dispatch: distinct requests never cross-talk, no dispatch is lost")]
    [InlineData(ExecutionScopeMode.Current)]
    [InlineData(ExecutionScopeMode.New)]
    public async Task ConcurrentDispatch_IsCorrect(ExecutionScopeMode scopeMode)
    {
        var sink = new ContentionSink();
        var provider = BuildProvider(sink, b => b.ConfigureDispatcher(o => o.ScopeMode = scopeMode));
        using var _ = provider;

        using var scope = provider.CreateScope();
        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        var inputs = Enumerable.Range(0, Fanout * Iterations).ToArray();
        var results = new ConcurrentDictionary<int, int>();

        await Parallel.ForEachAsync(
            inputs,
            new ParallelOptions { MaxDegreeOfParallelism = Fanout },
            async (n, ct) =>
            {
                // Query: result is derived purely from the request, so any cross-talk corrupts it.
                var squared = await cqrs.Send(new SquareQuery { Value = n }, ct);
                results[n] = squared;

                // Command: a shared Interlocked tally proves no dispatch is silently dropped.
                var commandResult = await cqrs.Send(new TallyCommand(), ct);
                commandResult.IsSuccess.Should().BeTrue();
            });

        results.Should().HaveCount(inputs.Length);
        results.Should().OnlyContain(kvp => kvp.Value == kvp.Key * kvp.Key, "each query result must match its own input");
        sink.CommandCount.Should().Be(inputs.Length, "every command dispatch must run exactly once");
    }

    // 2) The executor compacts the exempted behaviors out of the resolved array and sorts the rest in place. With every
    //    behavior a singleton, Microsoft DI resolves the same cached array for every dispatch in the scope, so each
    //    dispatch must work on its own copy: compacting the shared one would drop the exempted behavior from it and
    //    duplicate another, and every later dispatch would run that one twice.
    [Fact(DisplayName = "Concurrent dispatch over shared singleton behaviors: each runs its exemptions and priority order on its own copy")]
    public async Task PipelineOrdering_IsStableUnderContention()
    {
        var sink = new ContentionSink();
        var provider = BuildProvider(sink, b => b.ConfigureDispatcher(o => o.ScopeMode = ExecutionScopeMode.Current), services =>
        {
            // The exempted behavior first, so compacting it away moves every other entry; the rest in reverse priority
            // order, so the sort has work to do.
            services.AddSingleton(typeof(IPipelineBehavior<,>), typeof(ExemptedBehavior<,>));
            services.AddSingleton(typeof(IPipelineBehavior<,>), typeof(HighBehavior<,>));
            services.AddSingleton(typeof(IPipelineBehavior<,>), typeof(MidBehavior<,>));
            services.AddSingleton(typeof(IPipelineBehavior<,>), typeof(LowBehavior<,>));
        });
        using var _ = provider;

        // One shared scope: every concurrent dispatch resolves the behaviors from the same scoped provider.
        using var scope = provider.CreateScope();
        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        var ids = Enumerable.Range(0, Fanout * Iterations).Select(_ => Guid.NewGuid()).ToArray();

        await Parallel.ForEachAsync(
            ids,
            new ParallelOptions { MaxDegreeOfParallelism = Fanout },
            async (id, ct) => await cqrs.Send(new OrderedQuery { ContentionId = id }, ct));

        sink.PipelineOrder.Should().HaveCount(ids.Length);
        foreach (var entry in sink.PipelineOrder)
            entry.Value.Should().Equal(
                new[] { -100, 0, 100 },
                "every dispatch runs each behavior it is not exempted from exactly once, low to high priority");
    }

    // 3) Notification fan-out under contention: every handler must run exactly once per publish, with no lost or
    //    duplicated invocations across thousands of concurrent publishes.
    [Fact(DisplayName = "Notification fan-out: every handler runs exactly once per publish under contention")]
    public async Task NotificationFanout_IsCompleteUnderContention()
    {
        var sink = new ContentionSink();
        var provider = BuildProvider(sink);
        using var _ = provider;

        using var scope = provider.CreateScope();
        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        var count = Fanout * Iterations;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, count),
            new ParallelOptions { MaxDegreeOfParallelism = Fanout },
            async (_, ct) => await cqrs.Publish(new FanoutNotification(), ct));

        sink.HandlerCounts.Should().Be(
            (count, count, count),
            "each of the three handlers must run exactly once for every publish");
    }

    // 4) RunMode.Queued funnels every dispatch through the bounded background queue. Under contention the queue must
    //    carry each result back to its caller (via the completion source) with no hang and no cross-talk.
    [Fact(DisplayName = "RunMode.Queued: results flow back through the queue under contention, no hang")]
    public async Task RunModeQueued_FlowsResultsBackUnderContention()
    {
        var sink = new ContentionSink();
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(sink);
                services.AddCqrsGenerated(b => b.ConfigureDispatcher(o => o.RunMode = RunMode.Queued));
            })
            .Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var scope = host.Services.CreateScope();
            var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

            var inputs = Enumerable.Range(0, Fanout * 8).ToArray();
            var results = new ConcurrentDictionary<int, int>();

            await Parallel.ForEachAsync(
                inputs,
                new ParallelOptions { MaxDegreeOfParallelism = Fanout, CancellationToken = TestContext.Current.CancellationToken },
                async (n, ct) => results[n] = await cqrs.Send(new SquareQuery { Value = n }, ct));

            results.Should().HaveCount(inputs.Length);
            results.Should().OnlyContain(kvp => kvp.Value == kvp.Key * kvp.Key);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    // 5) HAZARD DEMONSTRATION (the one real footgun): a parallel publish strategy, which an application opts into,
    //    runs a notification's handlers side by side in the publisher's scope, so two handlers that share a
    //    non-thread-safe scoped service (an EF DbContext, say) overlap on it.
    [Theory(DisplayName = "HAZARD: an opted-in parallel strategy overlaps handlers that share a scoped resource")]
    [InlineData(PublishStrategy.Parallel)]
    [InlineData(PublishStrategy.ParallelWhenAllAggregate)]
    public async Task ParallelStrategies_OverlapHandlersSharingAScopedResource(PublishStrategy strategy)
    {
        using var provider = BuildSharedScopeProvider(strategy);
        using var scope = provider.CreateScope();
        var resource = scope.ServiceProvider.GetRequiredService<NonReentrantResource>();

        var publish = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish(new SharedScopeNotification(), CancellationToken.None);

        // The second handler starts while the first still holds the resource.
        await resource.SecondUse.Task.WaitAsync(TestContext.Current.CancellationToken);
        resource.Release.SetResult();
        (await FluentActions.Awaiting(() => publish).Should().ThrowAsync<InvalidOperationException>(
                "a parallel strategy starts the second handler while the first still uses the shared scoped resource"))
            .WithMessage("*Concurrent use*");
    }

    [Fact(DisplayName = "The default publish strategy is Sequential: a handler starts only once the one before it finished")]
    public async Task DefaultStrategy_RunsHandlersOneAtATime()
    {
        using var provider = BuildSharedScopeProvider(strategy: null);
        using var scope = provider.CreateScope();
        var resource = scope.ServiceProvider.GetRequiredService<NonReentrantResource>();

        var publish = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish(new SharedScopeNotification(), CancellationToken.None);

        await resource.FirstUse.Task.WaitAsync(TestContext.Current.CancellationToken);
        resource.SecondUse.Task.IsCompleted.Should().BeFalse("the second handler waits for the first, which holds the resource");
        resource.Release.SetResult();
        await publish;
        resource.SecondUse.Task.IsCompleted.Should().BeTrue("both handlers ran");
    }

    private static ServiceProvider BuildProvider(
        ContentionSink sink,
        Action<global::CQRSharp.Pipelines.ICqrsBuilder>? configure = null,
        Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sink);
        extra?.Invoke(services);
        services.AddCqrsGenerated(b => configure?.Invoke(b));
        return services.BuildServiceProvider();
    }

    // A null strategy leaves the notification options alone, so the provider publishes with the default.
    private static ServiceProvider BuildSharedScopeProvider(PublishStrategy? strategy)
    {
        var services = new ServiceCollection();
        services.AddScoped<NonReentrantResource>();
        services.AddCqrsGenerated(b =>
        {
            if (strategy is { } chosen) b.ConfigureNotifications(o => o.PublishStrategy = chosen);
        });
        return services.BuildServiceProvider();
    }

    // ---- shared sink ----

    public sealed class ContentionSink
    {
        private int _commandCount;
        private int _h1;
        private int _h2;
        private int _h3;

        public int CommandCount => Volatile.Read(ref _commandCount);
        public (int, int, int) HandlerCounts => (Volatile.Read(ref _h1), Volatile.Read(ref _h2), Volatile.Read(ref _h3));

        public ConcurrentDictionary<Guid, ConcurrentQueue<int>> PipelineOrder { get; } = new();

        public void CountCommand() => Interlocked.Increment(ref _commandCount);
        public void Handler1() => Interlocked.Increment(ref _h1);
        public void Handler2() => Interlocked.Increment(ref _h2);
        public void Handler3() => Interlocked.Increment(ref _h3);

        public void RecordBehavior(Guid id, int priority) =>
            PipelineOrder.GetOrAdd(id, static _ => new ConcurrentQueue<int>()).Enqueue(priority);
    }

    public interface IContentionTracked
    {
        Guid ContentionId { get; }
    }

    // ---- (1) dispatch correctness ----

    public sealed class SquareQuery : QueryBase<int>
    {
        public int Value { get; init; }
    }

    public sealed class SquareQueryHandler : IQueryHandler<SquareQuery, int>
    {
        public Task<int> Handle(SquareQuery query, CancellationToken cancellationToken)
            => Task.FromResult(query.Value * query.Value);
    }

    public sealed class TallyCommand : CommandBase;

    public sealed class TallyCommandHandler(ContentionSink sink) : ICommandHandler<TallyCommand>
    {
        public Task<CommandResult> Handle(TallyCommand command, CancellationToken cancellationToken)
        {
            sink.CountCommand();
            return Task.FromResult(CommandResult.FromSuccess());
        }
    }

    // ---- (2) pipeline ordering ----

    [PipelineExemption(typeof(ExemptedBehavior<,>))]
    public sealed class OrderedQuery : QueryBase<int>, IContentionTracked
    {
        public Guid ContentionId { get; init; }
    }

    public sealed class OrderedQueryHandler : IQueryHandler<OrderedQuery, int>
    {
        public Task<int> Handle(OrderedQuery query, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    public abstract class OrderRecordingBehavior<TRequest, TResult>(ContentionSink sink, int priority)
        : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior
        where TRequest : IRequest
    {
        public int PipelineExecutionPriority => priority;

        public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken)
        {
            if (request is IContentionTracked tracked)
                sink.RecordBehavior(tracked.ContentionId, priority);
            return next(cancellationToken);
        }
    }

    public sealed class LowBehavior<TRequest, TResult>(ContentionSink sink)
        : OrderRecordingBehavior<TRequest, TResult>(sink, -100) where TRequest : IRequest;

    public sealed class MidBehavior<TRequest, TResult>(ContentionSink sink)
        : OrderRecordingBehavior<TRequest, TResult>(sink, 0) where TRequest : IRequest;

    public sealed class HighBehavior<TRequest, TResult>(ContentionSink sink)
        : OrderRecordingBehavior<TRequest, TResult>(sink, 100) where TRequest : IRequest;

    // Recorded like the others, so a dispatch that runs it despite the exemption fails the order check.
    public sealed class ExemptedBehavior<TRequest, TResult>(ContentionSink sink)
        : OrderRecordingBehavior<TRequest, TResult>(sink, int.MinValue) where TRequest : IRequest;

    // ---- (3) notification fan-out ----

    public sealed class FanoutNotification : INotification;

    public sealed class FanoutHandler1(ContentionSink sink) : INotificationHandler<FanoutNotification>
    {
        public Task Handle(FanoutNotification notification, CancellationToken cancellationToken)
        {
            sink.Handler1();
            return Task.CompletedTask;
        }
    }

    public sealed class FanoutHandler2(ContentionSink sink) : INotificationHandler<FanoutNotification>
    {
        public Task Handle(FanoutNotification notification, CancellationToken cancellationToken)
        {
            sink.Handler2();
            return Task.CompletedTask;
        }
    }

    public sealed class FanoutHandler3(ContentionSink sink) : INotificationHandler<FanoutNotification>
    {
        public Task Handle(FanoutNotification notification, CancellationToken cancellationToken)
        {
            sink.Handler3();
            return Task.CompletedTask;
        }
    }

    // ---- (5) shared-scope hazard ----

    // A stand-in for a non-thread-safe scoped service (e.g. an EF Core DbContext): it throws if a second use overlaps a
    // first. Scoped, so the two notification handlers below share one instance per publish. The first use holds it until
    // the test releases it, so whether the second use overlaps is decided by the publish strategy alone.
    public sealed class NonReentrantResource
    {
        private int _inUse;
        private int _uses;

        public TaskCompletionSource FirstUse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondUse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task UseAsync()
        {
            (Interlocked.Increment(ref _uses) == 1 ? FirstUse : SecondUse).TrySetResult();
            if (Interlocked.Exchange(ref _inUse, 1) != 0)
                throw new InvalidOperationException("Concurrent use of a non-thread-safe scoped resource was detected.");

            try
            {
                await Release.Task;
            }
            finally
            {
                Volatile.Write(ref _inUse, 0);
            }
        }
    }

    public sealed class SharedScopeNotification : INotification;

    public sealed class SharedScopeHandlerA(NonReentrantResource resource) : INotificationHandler<SharedScopeNotification>
    {
        public Task Handle(SharedScopeNotification notification, CancellationToken cancellationToken)
            => resource.UseAsync();
    }

    public sealed class SharedScopeHandlerB(NonReentrantResource resource) : INotificationHandler<SharedScopeNotification>
    {
        public Task Handle(SharedScopeNotification notification, CancellationToken cancellationToken)
            => resource.UseAsync();
    }
}
