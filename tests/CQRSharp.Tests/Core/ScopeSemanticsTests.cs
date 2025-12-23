using CQRSharp.Core.Extensions;
using CQRSharp.Core.Mediation;
using CQRSharp.Core.Options;
using CQRSharp.Core.Options.Enums;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

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
        var result = await cqrs.Send(new NestedSendScopeQuery());

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
        var result = await cqrs.Send(new PublishScopeCheckQuery());

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
        var result = await cqrs.Send(new NestedSendScopeQuery());

        result.Should().BeFalse();
    }
}
