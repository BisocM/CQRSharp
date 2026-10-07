using BenchmarkDotNet.Attributes;
using Benchmarks.CqrSharp;
using CQRSharp;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
///     What a request's context costs a CQRSharp dispatch, measured as <see cref="InScopeDispatchBenchmarks" /> measures a
///     dispatch (the dispatcher resolved once, a new request per operation): the built-in context, which the dispatcher
///     builds itself and stamps with one read of the application's clock, against a custom context from a factory, built
///     with the parameterless constructor (which the dispatcher stamps) or with an explicit time.
/// </summary>
/// <remarks>
///     A context's creation time is read from the clock when the request is sent, whether or not anything reads it:
///     that read is what the timestamp means. The parameterless constructor also reads the system clock, for a context
///     built outside a dispatch, and the dispatcher then stamps it from the application's clock; a factory that passes
///     the time itself reads the clock once.
/// </remarks>
[MemoryDiagnoser]
public class RequestContextBenchmarks
{
    private ServiceProvider _provider = null!;
    private AsyncServiceScope _scope;
    private ICqrsDispatcher _cqrs = null!;

    [GlobalSetup]
    public void Setup()
    {
        _provider = CqrSharpSubject.CreateProvider(withBehavior: false);
        _scope = _provider.CreateAsyncScope();
        _cqrs = _scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _scope.DisposeAsync();
        await _provider.DisposeAsync();
    }

    [Benchmark(Baseline = true, Description = "Built-in context")]
    public Task<int> BuiltInContext() => _cqrs.Send(new Ping());

    [Benchmark(Description = "Custom context, parameterless constructor")]
    public Task<int> CustomContextStampedByTheDispatcher() => _cqrs.Send(new PingWithTenant());

    [Benchmark(Description = "Custom context, explicit time")]
    public Task<int> CustomContextWithItsOwnTime() => _cqrs.Send(new PingWithTimedTenant());
}
