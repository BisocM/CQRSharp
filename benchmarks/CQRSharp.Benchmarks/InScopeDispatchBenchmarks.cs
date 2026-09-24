using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Benchmarks.CqrSharp;
using Benchmarks.MediatorLib;
using Benchmarks.MediatRLib;
using CQRSharp;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
///     The cost of one dispatch with the dispatcher already resolved: each library resolves its mediator once, from a DI
///     scope it keeps for the whole run, and every operation dispatches through it. This isolates the dispatch itself,
///     which is what a worker, or a handler that dispatches many times from one scope, pays per call.
///     <see cref="NewScopeDispatchBenchmarks" /> measures what one dispatch per request scope costs.
/// </summary>
/// <remarks>
///     Handlers do nothing, so the numbers are each library's own cost. Every operation dispatches a new request object
///     (CQRSharp stamps a context onto the instance, so reusing one would skip work). A CQRSharp dispatch also creates a
///     request context; its lifecycle notifications, tracing and metrics are pay-for-use and nothing subscribes to them.
/// </remarks>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class InScopeDispatchBenchmarks
{
    private const string MediatRLabel = "MediatR 12.5";
    private const string MediatorLabel = "Mediator 3.0 (source-gen)";
    private const string CqrSharpLabel = "CQRSharp";

    // Disposed in reverse: each scope before its provider.
    private readonly Stack<IAsyncDisposable> _owned = new();

    private ICqrsDispatcher _cqrSharp = null!;
    private ICqrsDispatcher _cqrSharpWithBehavior = null!;
    private Mediator.IMediator _mediator = null!;
    private Mediator.IMediator _mediatorWithBehavior = null!;
    private MediatR.IMediator _mediatR = null!;
    private MediatR.IMediator _mediatRWithBehavior = null!;

    [GlobalSetup]
    public void Setup()
    {
        _cqrSharp = ResolveInScope<ICqrsDispatcher>(CqrSharpSubject.CreateProvider(withBehavior: false));
        _cqrSharpWithBehavior = ResolveInScope<ICqrsDispatcher>(CqrSharpSubject.CreateProvider(withBehavior: true));
        _mediatR = ResolveInScope<MediatR.IMediator>(MediatRSubject.CreateProvider(withBehavior: false));
        _mediatRWithBehavior = ResolveInScope<MediatR.IMediator>(MediatRSubject.CreateProvider(withBehavior: true));
        _mediator = ResolveInScope<Mediator.IMediator>(MediatorSubject.CreateProvider(withBehavior: false));
        _mediatorWithBehavior = ResolveInScope<Mediator.IMediator>(MediatorSubject.CreateProvider(withBehavior: true));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        while (_owned.TryPop(out var owned))
            await owned.DisposeAsync();
    }

    private T ResolveInScope<T>(ServiceProvider provider) where T : notnull
    {
        _owned.Push(provider);
        var scope = provider.CreateAsyncScope();
        _owned.Push(scope);
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    [BenchmarkCategory("Request"), Benchmark(Baseline = true, Description = MediatRLabel)]
    public Task<int> Request_MediatR() => _mediatR.Send(new Benchmarks.MediatRLib.Ping());

    [BenchmarkCategory("Request"), Benchmark(Description = MediatorLabel)]
    public ValueTask<int> Request_Mediator() => _mediator.Send(new Benchmarks.MediatorLib.Ping());

    [BenchmarkCategory("Request"), Benchmark(Description = CqrSharpLabel)]
    public Task<int> Request_CqrSharp() => _cqrSharp.Send(new Benchmarks.CqrSharp.Ping());

    [BenchmarkCategory("Request + 1 behavior"), Benchmark(Baseline = true, Description = MediatRLabel)]
    public Task<int> Behavior_MediatR() => _mediatRWithBehavior.Send(new Benchmarks.MediatRLib.Ping());

    [BenchmarkCategory("Request + 1 behavior"), Benchmark(Description = MediatorLabel)]
    public ValueTask<int> Behavior_Mediator() => _mediatorWithBehavior.Send(new Benchmarks.MediatorLib.Ping());

    [BenchmarkCategory("Request + 1 behavior"), Benchmark(Description = CqrSharpLabel)]
    public Task<int> Behavior_CqrSharp() => _cqrSharpWithBehavior.Send(new Benchmarks.CqrSharp.Ping());

    [BenchmarkCategory("Notification"), Benchmark(Baseline = true, Description = MediatRLabel)]
    public Task Notification_MediatR() => _mediatR.Publish(new Benchmarks.MediatRLib.Pinged());

    [BenchmarkCategory("Notification"), Benchmark(Description = MediatorLabel)]
    public ValueTask Notification_Mediator() => _mediator.Publish(new Benchmarks.MediatorLib.Pinged());

    [BenchmarkCategory("Notification"), Benchmark(Description = CqrSharpLabel)]
    public Task Notification_CqrSharp() => _cqrSharp.Publish(new Benchmarks.CqrSharp.Pinged());

    // Published under the notification interface, as a list of domain events is: the library finds the handlers from the
    // runtime type instead of the static one.
    [BenchmarkCategory("Notification as INotification"), Benchmark(Baseline = true, Description = MediatRLabel)]
    public Task NotificationAsInterface_MediatR() => _mediatR.Publish<MediatR.INotification>(new Benchmarks.MediatRLib.Pinged());

    [BenchmarkCategory("Notification as INotification"), Benchmark(Description = MediatorLabel)]
    public ValueTask NotificationAsInterface_Mediator() => _mediator.Publish<Mediator.INotification>(new Benchmarks.MediatorLib.Pinged());

    [BenchmarkCategory("Notification as INotification"), Benchmark(Description = CqrSharpLabel)]
    public Task NotificationAsInterface_CqrSharp() => _cqrSharp.Publish<INotification>(new Benchmarks.CqrSharp.Pinged());

    // A three-item stream, enumerated to the end: the dispatch plus the per-item cost of whatever wraps the handler's stream.
    [BenchmarkCategory("Stream (3 items)"), Benchmark(Baseline = true, Description = MediatRLabel)]
    public async Task<int> Stream_MediatR()
    {
        var sum = 0;
        await foreach (var item in _mediatR.CreateStream(new Benchmarks.MediatRLib.PingStream())) sum += item;
        return sum;
    }

    [BenchmarkCategory("Stream (3 items)"), Benchmark(Description = MediatorLabel)]
    public async Task<int> Stream_Mediator()
    {
        var sum = 0;
        await foreach (var item in _mediator.CreateStream(new Benchmarks.MediatorLib.PingStream())) sum += item;
        return sum;
    }

    [BenchmarkCategory("Stream (3 items)"), Benchmark(Description = CqrSharpLabel)]
    public async Task<int> Stream_CqrSharp()
    {
        var sum = 0;
        await foreach (var item in _cqrSharp.Stream(new Benchmarks.CqrSharp.PingStream())) sum += item;
        return sum;
    }
}
