using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Benchmarks.CqrSharp;
using Benchmarks.MediatRLib;
using CQRSharp;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
///     What one dispatch per request scope costs, which is what a web request pays: every operation creates a DI scope,
///     resolves the library's mediator from it, dispatches once and disposes the scope. Everything a library builds per
///     scope is in these numbers; <see cref="InScopeDispatchBenchmarks" /> measures the dispatch alone.
/// </summary>
/// <remarks>
///     The scenarios, handlers and registrations are the ones <see cref="InScopeDispatchBenchmarks" /> uses. Each library
///     runs with its documented default lifetimes, so what a scope costs differs by design: MediatR's mediator and handlers are
///     transient, CQRSharp's dispatcher is scoped and its handlers transient.
/// </remarks>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class NewScopeDispatchBenchmarks
{
    private const string MediatRLabel = "MediatR 12.5";
    private const string CqrSharpLabel = "CQRSharp";

    private ServiceProvider _cqrSharp = null!;
    private ServiceProvider _cqrSharpWithBehavior = null!;
    private ServiceProvider _mediatR = null!;
    private ServiceProvider _mediatRWithBehavior = null!;

    [GlobalSetup]
    public void Setup()
    {
        _cqrSharp = CqrSharpSubject.CreateProvider(withBehavior: false);
        _cqrSharpWithBehavior = CqrSharpSubject.CreateProvider(withBehavior: true);
        _mediatR = MediatRSubject.CreateProvider(withBehavior: false);
        _mediatRWithBehavior = MediatRSubject.CreateProvider(withBehavior: true);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        foreach (var provider in new[] { _cqrSharp, _cqrSharpWithBehavior, _mediatR, _mediatRWithBehavior })
            await provider.DisposeAsync();
    }

    [BenchmarkCategory("Request in a new DI scope"), Benchmark(Baseline = true, Description = MediatRLabel)]
    public async Task<int> Request_MediatR()
    {
        await using var scope = _mediatR.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MediatR.IMediator>().Send(new Benchmarks.MediatRLib.Ping());
    }

    [BenchmarkCategory("Request in a new DI scope"), Benchmark(Description = CqrSharpLabel)]
    public async Task<int> Request_CqrSharp()
    {
        await using var scope = _cqrSharp.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new Benchmarks.CqrSharp.Ping());
    }

    [BenchmarkCategory("Request + 1 behavior in a new DI scope"), Benchmark(Baseline = true, Description = MediatRLabel)]
    public async Task<int> Behavior_MediatR()
    {
        await using var scope = _mediatRWithBehavior.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MediatR.IMediator>().Send(new Benchmarks.MediatRLib.Ping());
    }

    [BenchmarkCategory("Request + 1 behavior in a new DI scope"), Benchmark(Description = CqrSharpLabel)]
    public async Task<int> Behavior_CqrSharp()
    {
        await using var scope = _cqrSharpWithBehavior.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new Benchmarks.CqrSharp.Ping());
    }

    [BenchmarkCategory("Notification in a new DI scope"), Benchmark(Baseline = true, Description = MediatRLabel)]
    public async Task Notification_MediatR()
    {
        await using var scope = _mediatR.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<MediatR.IMediator>().Publish(new Benchmarks.MediatRLib.Pinged());
    }

    [BenchmarkCategory("Notification in a new DI scope"), Benchmark(Description = CqrSharpLabel)]
    public async Task Notification_CqrSharp()
    {
        await using var scope = _cqrSharp.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish(new Benchmarks.CqrSharp.Pinged());
    }

    [BenchmarkCategory("Notification as INotification in a new DI scope"), Benchmark(Baseline = true, Description = MediatRLabel)]
    public async Task NotificationAsInterface_MediatR()
    {
        await using var scope = _mediatR.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<MediatR.IMediator>().Publish<MediatR.INotification>(new Benchmarks.MediatRLib.Pinged());
    }

    [BenchmarkCategory("Notification as INotification in a new DI scope"), Benchmark(Description = CqrSharpLabel)]
    public async Task NotificationAsInterface_CqrSharp()
    {
        await using var scope = _cqrSharp.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish<INotification>(new Benchmarks.CqrSharp.Pinged());
    }

    [BenchmarkCategory("Stream (3 items) in a new DI scope"), Benchmark(Baseline = true, Description = MediatRLabel)]
    public async Task<int> Stream_MediatR()
    {
        await using var scope = _mediatR.CreateAsyncScope();
        var sum = 0;
        await foreach (var item in scope.ServiceProvider.GetRequiredService<MediatR.IMediator>().CreateStream(new Benchmarks.MediatRLib.PingStream()))
            sum += item;
        return sum;
    }

    [BenchmarkCategory("Stream (3 items) in a new DI scope"), Benchmark(Description = CqrSharpLabel)]
    public async Task<int> Stream_CqrSharp()
    {
        await using var scope = _cqrSharp.CreateAsyncScope();
        var sum = 0;
        await foreach (var item in scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Stream(new Benchmarks.CqrSharp.PingStream()))
            sum += item;
        return sum;
    }
}
