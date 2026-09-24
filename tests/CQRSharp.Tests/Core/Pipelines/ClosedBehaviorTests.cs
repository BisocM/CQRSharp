using System.Runtime.CompilerServices;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Closed behavior resolution: the generated per-request behavior sets run every applicable open-generic behavior
///     (value-type results and streams included), see late registrations once, describe streams as the runtime runs
///     them, keep one instance per closed type for each lifetime, and are disposed the way the container disposes what
///     it creates: with their provider, before their own dependencies, and never synchronously when they can only be
///     disposed asynchronously.
/// </summary>
public sealed class ClosedBehaviorTests
{
    [Fact(DisplayName = "With closed behavior resolution, a value-type result request still runs every open-generic behavior")]
    public async Task Value_type_results_run_their_behaviors_through_the_closed_set()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ClosedBehaviorResolution { Enabled = true });
        services.AddScoped<BehaviorProbe>();
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(RecordingBehavior<,>));
        services.AddTransient(typeof(IStreamPipelineBehavior<,>), typeof(RecordingStreamBehavior<,>));
        services.AddCqrsGenerated();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var probe = scope.ServiceProvider.GetRequiredService<BehaviorProbe>();

        (await dispatcher.Send(new CountingQuery(), TestContext.Current.CancellationToken)).Should().Be(42);
        (await dispatcher.Send(new NamingQuery(), TestContext.Current.CancellationToken)).Should().Be("name");
        var items = new List<int>();
        await foreach (var item in dispatcher.Stream(new TestStreamRequest(3), TestContext.Current.CancellationToken)) items.Add(item);

        items.Should().Equal(0, 1, 2);
        probe.Requests.Should().Equal(nameof(CountingQuery), nameof(NamingQuery));
        probe.Streams.Should().Equal(nameof(TestStreamRequest));
    }

    [Fact(DisplayName = "Closed behaviors: a behavior registered after AddCqrsGenerated applies, once, even with AddCqrsGenerated called twice")]
    public async Task Closed_behaviors_see_late_registrations_once()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ClosedBehaviorResolution { Enabled = true });
        services.AddScoped<BehaviorProbe>();
        services.AddCqrsGenerated();
        services.AddCqrsGenerated();
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(RecordingBehavior<,>));
        services.AddTransient(typeof(IStreamPipelineBehavior<,>), typeof(RecordingStreamBehavior<,>));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        (await dispatcher.Send(new CountingQuery(), TestContext.Current.CancellationToken)).Should().Be(42);
        await foreach (var _ in dispatcher.Stream(new TestStreamRequest(1), TestContext.Current.CancellationToken)) { }

        var probe = scope.ServiceProvider.GetRequiredService<BehaviorProbe>();
        probe.Requests.Should().Equal(nameof(CountingQuery));
        probe.Streams.Should().Equal(nameof(TestStreamRequest));
    }

    [Fact(DisplayName = "Closed behaviors: the generated diagnostics describe a value-type stream without CQRDIAG004")]
    public async Task Closed_behaviors_describe_streams_like_the_runtime()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ClosedBehaviorResolution { Enabled = true });
        services.AddScoped<BehaviorProbe>();
        services.AddCqrsGenerated();
        services.AddTransient(typeof(IStreamPipelineBehavior<,>), typeof(RecordingStreamBehavior<,>));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var binding = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>().DescribeAllRequests()
            .Single(b => b.RequestType == typeof(TestStreamRequest));

        binding.Issues.Should().NotContain(i => i.Code == "CQRDIAG004");
    }

    [Fact(DisplayName = "Closed behaviors are disposed with their scope, as the container disposes the ones it creates")]
    public async Task Closed_behaviors_are_disposed()
    {
        var tracker = new BehaviorDisposalTracker();
        var services = new ServiceCollection();
        services.AddSingleton(new ClosedBehaviorResolution { Enabled = true });
        services.AddSingleton(tracker);
        services.AddCqrsGenerated();
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(DisposableBehavior<,>));
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new CountingQuery(), TestContext.Current.CancellationToken)).Should().Be(42);

        tracker.Disposed.Should().Be(1);
    }

    [Theory(DisplayName = "Closed behaviors: an open-generic behavior serves two value-type requests with its own instance for each, whatever its lifetime")]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Transient)]
    public async Task Each_closed_type_gets_its_own_instance(ServiceLifetime lifetime)
    {
        IServiceCollection services = new ServiceCollection();
        services.AddSingleton(new ClosedBehaviorResolution { Enabled = true });
        // A singleton: a singleton behavior is built from the root and would otherwise capture the root's scoped probe.
        services.AddSingleton<BehaviorProbe>();
        services.AddScoped<ScopedMarker>();
        services.AddCqrsGenerated();
        services.Add(new ServiceDescriptor(typeof(IPipelineBehavior<,>), typeof(RecordingBehavior<,>), lifetime));
        services.Add(new ServiceDescriptor(typeof(IStreamPipelineBehavior<,>), typeof(RecordingStreamBehavior<,>), lifetime));
        await using var provider = services.BuildServiceProvider();
        var probe = provider.GetRequiredService<BehaviorProbe>();

        for (var round = 0; round < 2; round++)
        {
            await using var scope = provider.CreateAsyncScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

            (await dispatcher.Send(new CountingQuery(), TestContext.Current.CancellationToken)).Should().Be(42);
            await dispatcher.Send(new GetScopedMarkerIdQuery(), TestContext.Current.CancellationToken);
            await foreach (var _ in dispatcher.Stream(new TestStreamRequest(1), TestContext.Current.CancellationToken)) { }
            await foreach (var _ in dispatcher.Stream(new CountdownStream(), TestContext.Current.CancellationToken)) { }
        }

        probe.Requests.Should().Equal(
            nameof(CountingQuery), nameof(GetScopedMarkerIdQuery), nameof(CountingQuery), nameof(GetScopedMarkerIdQuery));
        probe.Streams.Should().Equal(
            nameof(TestStreamRequest), nameof(CountdownStream), nameof(TestStreamRequest), nameof(CountdownStream));
    }

    [Theory(DisplayName = "Closed behaviors are disposed before the dependencies they were built with, as the container orders it")]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Transient)]
    public async Task Behaviors_are_disposed_before_their_dependencies(ServiceLifetime lifetime)
    {
        var log = new DisposalLog();
        IServiceCollection services = new ServiceCollection();
        services.AddSingleton(new ClosedBehaviorResolution { Enabled = true });
        services.AddSingleton(log);
        // The sink shares the behavior's lifetime (a singleton behavior cannot take a scoped one) and nothing else uses
        // it, so it is created while the behavior is built.
        services.Add(new ServiceDescriptor(typeof(FlushSink), typeof(FlushSink), lifetime == ServiceLifetime.Singleton ? ServiceLifetime.Singleton : ServiceLifetime.Scoped));
        services.AddCqrsGenerated();
        services.Add(new ServiceDescriptor(typeof(IPipelineBehavior<,>), typeof(FlushingBehavior<,>), lifetime));

        await using (var provider = services.BuildServiceProvider())
        await using (var scope = provider.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new CountingQuery(), TestContext.Current.CancellationToken)).Should().Be(42);

        log.Entries.Should().Equal("behavior flushed into a live sink", "sink disposed");
    }

    [Fact(DisplayName = "Closed behaviors: a behavior that only disposes asynchronously fails a synchronous scope disposal, as the container does, and is disposed by an asynchronous one")]
    public async Task Async_only_behaviors_are_not_disposed_synchronously()
    {
        var tracker = new BehaviorDisposalTracker();
        var services = new ServiceCollection();
        services.AddSingleton(new ClosedBehaviorResolution { Enabled = true });
        services.AddSingleton(tracker);
        services.AddCqrsGenerated();
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(AsyncOnlyDisposableBehavior<,>));
        await using var provider = services.BuildServiceProvider();

        var syncScope = provider.CreateScope();
        (await syncScope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new CountingQuery(), TestContext.Current.CancellationToken)).Should().Be(42);
        syncScope.Invoking(s => s.Dispose()).Should().Throw<InvalidOperationException>()
            .WithMessage("*AsyncOnlyDisposableBehavior*only implements IAsyncDisposable*");
        tracker.Disposed.Should().Be(0, "nothing blocks on the behavior's asynchronous disposal");

        await using (var asyncScope = provider.CreateAsyncScope())
            (await asyncScope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new CountingQuery(), TestContext.Current.CancellationToken)).Should().Be(42);
        tracker.Disposed.Should().Be(1);
    }

    // UnclosableBehavior is private, so the generated code of this assembly cannot close it over CountingQuery and records
    // the gap instead.
    [Fact(DisplayName = "Closed behaviors: a registered behavior generated code could not close fails the dispatch and CQRDIAG004 instead of being left out")]
    public async Task A_behavior_that_could_not_be_closed_is_refused()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ClosedBehaviorResolution { Enabled = true });
        services.AddCqrsGenerated();
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(UnclosableBehavior<,>));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var send = () => scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new CountingQuery(), TestContext.Current.CancellationToken);

        (await send.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain(typeof(UnclosableBehavior<,>).FullName!).And.Contain(typeof(CountingQuery).FullName!);
        scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>().DescribeAllRequests()
            .Single(b => b.RequestType == typeof(CountingQuery))
            .Issues.Should().ContainSingle(i => i.Code == "CQRDIAG004")
            .Which.Message.Should().Contain(typeof(UnclosableBehavior<,>).FullName!);
    }

    [Fact(DisplayName = "Closed behaviors: a behavior whose constraints exclude the request needs no factory for it and is skipped")]
    public async Task A_behavior_that_does_not_apply_is_skipped()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ClosedBehaviorResolution { Enabled = true });
        services.AddCqrsGenerated();
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(CommandsOnlyBehavior<,>));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        (await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new CountingQuery(), TestContext.Current.CancellationToken)).Should().Be(42);
    }

    private sealed class UnclosableBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
    {
        public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
    }

    private sealed class CommandsOnlyBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : ICommand
    {
        public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
    }
}

public sealed class CountdownStream : StreamRequestBase<long>;

public sealed class CountdownStreamHandler : IStreamRequestHandler<CountdownStream, long>
{
    public async IAsyncEnumerable<long> Handle(CountdownStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return 1;
    }
}

public sealed class DisposalLog
{
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_entries) return _entries.ToArray();
        }
    }

    public void Add(string entry)
    {
        lock (_entries) _entries.Add(entry);
    }
}

public sealed class FlushSink(DisposalLog log) : IDisposable
{
    public bool Disposed { get; private set; }

    public void Dispose()
    {
        Disposed = true;
        log.Add("sink disposed");
    }
}

public sealed class FlushingBehavior<TRequest, TResult>(FlushSink sink, DisposalLog log) : IPipelineBehavior<TRequest, TResult>, IDisposable
    where TRequest : IRequest
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);

    public void Dispose() => log.Add(sink.Disposed ? "behavior flushed into a disposed sink" : "behavior flushed into a live sink");
}

public sealed class AsyncOnlyDisposableBehavior<TRequest, TResult>(BehaviorDisposalTracker tracker) : IPipelineBehavior<TRequest, TResult>, IAsyncDisposable
    where TRequest : IRequest
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        Interlocked.Increment(ref tracker.Disposed);
    }
}

public sealed class CountingQuery : QueryBase<int>;

public sealed class CountingQueryHandler : IQueryHandler<CountingQuery, int>
{
    public Task<int> Handle(CountingQuery query, CancellationToken cancellationToken) => Task.FromResult(42);
}

public sealed class BehaviorProbe
{
    public List<string> Requests { get; } = new();
    public List<string> Streams { get; } = new();
}

public sealed class RecordingBehavior<TRequest, TResult>(BehaviorProbe probe) : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken)
    {
        probe.Requests.Add(typeof(TRequest).Name);
        return next(cancellationToken);
    }
}

public sealed class RecordingStreamBehavior<TRequest, TItem>(BehaviorProbe probe) : IStreamPipelineBehavior<TRequest, TItem> where TRequest : IRequest
{
    public IAsyncEnumerable<TItem> Handle(TRequest request, StreamHandlerDelegate<TItem> next, CancellationToken cancellationToken)
    {
        probe.Streams.Add(typeof(TRequest).Name);
        return next(cancellationToken);
    }
}

public sealed class BehaviorDisposalTracker
{
    public int Disposed;
}

public sealed class DisposableBehavior<TRequest, TResult>(BehaviorDisposalTracker tracker) : IPipelineBehavior<TRequest, TResult>, IDisposable
    where TRequest : IRequest
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);

    public void Dispose() => Interlocked.Increment(ref tracker.Disposed);
}
