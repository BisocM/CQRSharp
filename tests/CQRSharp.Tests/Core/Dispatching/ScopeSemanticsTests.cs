using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Which DI scope a request runs in (<see cref="ExecutionScopeMode" />): by default a nested send, a publish and a
///     stream run in the caller's scope; in <see cref="ExecutionScopeMode.New" /> each send and each stream enumeration
///     gets a scope of its own, which a stream keeps until its enumeration completes.
/// </summary>
public sealed class ScopeSemanticsTests
{
    [Fact]
    public async Task Default_scope_mode_uses_current_scope_for_nested_send()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddScoped<ScopedMarker>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var result = await cqrs.Send(new NestedSendScopeQuery(), TestContext.Current.CancellationToken);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Default_scope_mode_uses_current_scope_for_publish()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddScoped<ScopedMarker>();
        services.AddSingleton<ScopeCheckSink>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var result = await cqrs.Send(new PublishScopeCheckQuery(), TestContext.Current.CancellationToken);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task New_scope_mode_creates_new_scope_per_send()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddScoped<ScopedMarker>();
        services.Configure<DispatcherOptions>(options => { options.ScopeMode = ExecutionScopeMode.New; });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var result = await cqrs.Send(new NestedSendScopeQuery(), TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Default_scope_mode_uses_current_scope_for_stream_and_nested_send()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddScoped<ScopedMarker>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        var results = new List<bool>();
        await foreach (var item in cqrs.Stream(new NestedSendScopeStreamRequest(), TestContext.Current.CancellationToken))
            results.Add(item);

        results.Should().Equal(true);
    }

    [Fact]
    public async Task New_scope_mode_creates_new_scope_per_stream_enumeration()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddScoped<ScopedMarker>();
        services.Configure<DispatcherOptions>(options => { options.ScopeMode = ExecutionScopeMode.New; });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        var results = new List<bool>();
        await foreach (var item in cqrs.Stream(new NestedSendScopeStreamRequest(), TestContext.Current.CancellationToken))
            results.Add(item);

        results.Should().Equal(false);
    }

    [Fact]
    public async Task New_scope_mode_disposes_scope_after_stream_completes()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.Configure<DispatcherOptions>(options => { options.ScopeMode = ExecutionScopeMode.New; });
        services.AddSingleton<DisposalTracker>();
        services.AddScoped<ScopedDisposalProbe>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var tracker = scope.ServiceProvider.GetRequiredService<DisposalTracker>();

        var stream = cqrs.Stream(new StreamScopeDisposalRequest(), TestContext.Current.CancellationToken);
        await using var enumerator = stream.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        (await enumerator.MoveNextAsync()).Should().BeTrue();
        tracker.DisposedCount.Should().Be(0);

        (await enumerator.MoveNextAsync()).Should().BeTrue();
        tracker.DisposedCount.Should().Be(0);

        (await enumerator.MoveNextAsync()).Should().BeFalse();
        tracker.DisposedCount.Should().Be(1);
    }
}