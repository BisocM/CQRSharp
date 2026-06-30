using System.Collections.Concurrent;
using CQRSharp.Abstractions.Interfaces.Handlers;
using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Interfaces.Markers.Query;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Abstractions.Models.Commands;
using CQRSharp.Core.Extensions;
using CQRSharp.Core.Mediation;
using CQRSharp.Core.Options.Enums;
using CQRSharp.Core.Pipelines;
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

    // 2) The pipeline filters+sorts the DI-resolved behavior array in place. Under heavy concurrent dispatch sharing one
    //    scope, every single request must still observe the behaviors in strict priority order — proving the in-place
    //    mutation operates on a per-call array and is never corrupted by a sibling dispatch.
    [Fact(DisplayName = "Pipeline ordering is correct on every concurrent dispatch (in-place sort is contention-safe)")]
    public async Task PipelineOrdering_IsStableUnderContention()
    {
        var sink = new ContentionSink();
        var provider = BuildProvider(sink, b => b.ConfigureDispatcher(o => o.ScopeMode = ExecutionScopeMode.Current), services =>
        {
            // Registered in deliberately reversed priority order so the executor's sort actually has work to do.
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(HighBehavior<,>));
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(MidBehavior<,>));
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LowBehavior<,>));
        });
        using var _ = provider;

        // One shared scope ⇒ all concurrent dispatches hit the same PipelineExecutor and resolve behaviors from the
        // same scoped provider, maximizing contention on the in-place filter/sort.
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
                "behaviors must run low→high priority on every dispatch; a corrupted shared array would reorder them");
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

        await host.StartAsync();
        try
        {
            using var scope = host.Services.CreateScope();
            var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

            var inputs = Enumerable.Range(0, Fanout * 8).ToArray();
            var results = new ConcurrentDictionary<int, int>();

            // A hard ceiling so a regression that hangs the queue fails fast instead of stalling the suite.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            await Parallel.ForEachAsync(
                inputs,
                new ParallelOptions { MaxDegreeOfParallelism = Fanout, CancellationToken = timeout.Token },
                async (n, ct) => results[n] = await cqrs.Send(new SquareQuery { Value = n }, ct));

            results.Should().HaveCount(inputs.Length);
            results.Should().OnlyContain(kvp => kvp.Value == kvp.Key * kvp.Key);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // 5) HAZARD DEMONSTRATION (the one real footgun): the DEFAULT publish strategy runs handlers in parallel within one
    //    shared scope, so two handlers that share a non-thread-safe scoped service overlap. Sequential does not.
    [Fact(DisplayName = "HAZARD: default ParallelWhenAllAggregate overlaps handlers sharing a scoped resource; Sequential does not")]
    public async Task DefaultStrategy_OverlapsHandlersSharingAScopedResource()
    {
        // Default strategy (ParallelWhenAllAggregate): the two handlers run concurrently in the same scope and both
        // touch the same scoped NonReentrantResource ⇒ the resource's concurrency guard fires.
        var parallelProvider = BuildSharedScopeProvider(PublishStrategy.ParallelWhenAllAggregate);
        using (parallelProvider)
        {
            using var scope = parallelProvider.CreateScope();
            var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

            var act = () => cqrs.Publish(new SharedScopeNotification(), CancellationToken.None);
            (await act.Should().ThrowAsync<InvalidOperationException>(
                    "the default strategy runs notification handlers in parallel within one DI scope, so handlers " +
                    "sharing a non-thread-safe scoped service (e.g. an EF DbContext) overlap"))
                .WithMessage("*Concurrent use*");
        }

        // Sequential: handlers run one at a time, so the shared scoped resource is never used concurrently.
        var sequentialProvider = BuildSharedScopeProvider(PublishStrategy.Sequential);
        using (sequentialProvider)
        {
            using var scope = sequentialProvider.CreateScope();
            var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

            var act = () => cqrs.Publish(new SharedScopeNotification(), CancellationToken.None);
            await act.Should().NotThrowAsync("Sequential dispatch never overlaps handlers that share a scoped resource");
        }
    }

    private static ServiceProvider BuildProvider(
        ContentionSink sink,
        Action<global::CQRSharp.Pipelines.Extensions.ICqrsBuilder>? configure = null,
        Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sink);
        extra?.Invoke(services);
        services.AddCqrsGenerated(b => configure?.Invoke(b));
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildSharedScopeProvider(PublishStrategy strategy)
    {
        var services = new ServiceCollection();
        services.AddScoped<NonReentrantResource>();
        services.AddCqrsGenerated(b => b.ConfigureNotifications(o => o.PublishStrategy = strategy));
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

    // A stand-in for a non-thread-safe scoped service (e.g. an EF Core DbContext): it throws if a second operation
    // overlaps a first. Scoped, so the two notification handlers below share one instance per publish.
    public sealed class NonReentrantResource
    {
        private int _inUse;

        public async Task UseAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _inUse, 1) != 0)
                throw new InvalidOperationException("Concurrent use of a non-thread-safe scoped resource was detected.");

            try
            {
                await Task.Delay(40, cancellationToken).ConfigureAwait(false);
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
            => resource.UseAsync(cancellationToken);
    }

    public sealed class SharedScopeHandlerB(NonReentrantResource resource) : INotificationHandler<SharedScopeNotification>
    {
        public Task Handle(SharedScopeNotification notification, CancellationToken cancellationToken)
            => resource.UseAsync(cancellationToken);
    }
}
