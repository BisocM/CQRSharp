using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Benchmarks.CqrSharp;
using Benchmarks.MediatorLib;
using Benchmarks.MediatRLib;
using CQRSharp;
using Microsoft.Extensions.DependencyInjection;

BenchmarkSwitcher.FromAssembly(typeof(DispatchBenchmarks).Assembly).Run(args);

/// <summary>
///     Per-dispatch overhead of each library with a trivial handler, so the number is the framework's own cost. A new
///     request object is allocated per call for every library (CQRSharp stamps metadata/context onto the instance, so
///     reusing one would skip work and flatter it). All three dispatch from a single long-lived DI scope.
/// </summary>
/// <remarks>
///     Not an apples-to-apples feature comparison: a CQRSharp dispatch also publishes the *Initiated / *Completed
///     lifecycle notifications and starts a tracing activity, which the others do not do.
/// </remarks>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class DispatchBenchmarks
{
    private ICqrsDispatcher _cqrSharp = null!;
    private ICqrsDispatcher _cqrSharpWithBehavior = null!;
    private Mediator.IMediator _mediator = null!;
    private Mediator.IMediator _mediatorWithBehavior = null!;
    private MediatR.IMediator _mediatR = null!;
    private MediatR.IMediator _mediatRWithBehavior = null!;
    private IServiceProvider _cqrSharpProvider = null!;
    private IServiceProvider _mediatorProvider = null!;
    private IServiceProvider _mediatRProvider = null!;

    [GlobalSetup]
    public void Setup()
    {
        _cqrSharp = CqrSharpSubject.Create(withBehavior: false);
        _cqrSharpWithBehavior = CqrSharpSubject.Create(withBehavior: true);
        _mediatR = MediatRSubject.Create(withBehavior: false);
        _mediatRWithBehavior = MediatRSubject.Create(withBehavior: true);
        _mediator = MediatorSubject.Create(withBehavior: false);
        _mediatorWithBehavior = MediatorSubject.Create(withBehavior: true);
        _cqrSharpProvider = CqrSharpSubject.CreateProvider(withBehavior: false);
        _mediatRProvider = MediatRSubject.CreateProvider(withBehavior: false);
        _mediatorProvider = MediatorSubject.CreateProvider(withBehavior: false);
    }

    [BenchmarkCategory("Request"), Benchmark(Baseline = true, Description = "MediatR 12.5")]
    public Task<int> Request_MediatR() => _mediatR.Send(new Benchmarks.MediatRLib.Ping());

    [BenchmarkCategory("Request"), Benchmark(Description = "Mediator 3.0 (source-gen)")]
    public ValueTask<int> Request_Mediator() => _mediator.Send(new Benchmarks.MediatorLib.Ping());

    [BenchmarkCategory("Request"), Benchmark(Description = "CQRSharp")]
    public Task<int> Request_CqrSharp() => _cqrSharp.Send(new Benchmarks.CqrSharp.Ping());

    [BenchmarkCategory("Request + 1 behavior"), Benchmark(Baseline = true, Description = "MediatR 12.5")]
    public Task<int> Behavior_MediatR() => _mediatRWithBehavior.Send(new Benchmarks.MediatRLib.Ping());

    [BenchmarkCategory("Request + 1 behavior"), Benchmark(Description = "Mediator 3.0 (source-gen)")]
    public ValueTask<int> Behavior_Mediator() => _mediatorWithBehavior.Send(new Benchmarks.MediatorLib.Ping());

    [BenchmarkCategory("Request + 1 behavior"), Benchmark(Description = "CQRSharp")]
    public Task<int> Behavior_CqrSharp() => _cqrSharpWithBehavior.Send(new Benchmarks.CqrSharp.Ping());

    [BenchmarkCategory("Notification"), Benchmark(Baseline = true, Description = "MediatR 12.5")]
    public Task Notification_MediatR() => _mediatR.Publish(new Benchmarks.MediatRLib.Pinged());

    [BenchmarkCategory("Notification"), Benchmark(Description = "Mediator 3.0 (source-gen)")]
    public ValueTask Notification_Mediator() => _mediator.Publish(new Benchmarks.MediatorLib.Pinged());

    [BenchmarkCategory("Notification"), Benchmark(Description = "CQRSharp")]
    public Task Notification_CqrSharp() => _cqrSharp.Publish(new Benchmarks.CqrSharp.Pinged());

    // What a web request actually pays: a fresh DI scope, the dispatcher resolved from it, one dispatch, scope disposed.
    [BenchmarkCategory("Request in a new DI scope"), Benchmark(Baseline = true, Description = "MediatR 12.5")]
    public async Task<int> Scoped_MediatR()
    {
        await using var scope = _mediatRProvider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MediatR.IMediator>().Send(new Benchmarks.MediatRLib.Ping());
    }

    [BenchmarkCategory("Request in a new DI scope"), Benchmark(Description = "Mediator 3.0 (source-gen)")]
    public async Task<int> Scoped_Mediator()
    {
        await using var scope = _mediatorProvider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<Mediator.IMediator>().Send(new Benchmarks.MediatorLib.Ping());
    }

    [BenchmarkCategory("Request in a new DI scope"), Benchmark(Description = "CQRSharp")]
    public async Task<int> Scoped_CqrSharp()
    {
        await using var scope = _cqrSharpProvider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new Benchmarks.CqrSharp.Ping());
    }
}
