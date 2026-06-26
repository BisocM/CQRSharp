using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Core.Extensions;
using CQRSharp.Core.Mediation;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines.Behaviors.RateLimiting;
using CQRSharp.Pipelines.Extensions;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.PlugAndPlay;

public sealed class PlugAndPlayTests
{
    [Fact]
    public async Task AddCqrsGenerated_registers_dispatcher_and_executes_requests()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        var commandResult = await cqrs.Send(new TestCommand());
        commandResult.IsSuccess.Should().BeTrue();

        var queryResult = await cqrs.Send(new TestQuery());
        queryResult.Value.Should().Be("Success");
    }

    [Fact]
    public void AddCqrsGenerated_registers_aot_safe_outbox_notification_serializer()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();

        using var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<INotificationSerializer>();

        serializer.GetNotificationName(typeof(TestNotification)).Should().Be("test.notification");

        var payload = serializer.Serialize(new TestNotification());
        payload.Should().NotBeNull();
        payload.Length.Should().BeGreaterThan(0);

        serializer.Deserialize("unknown.notification", payload).Should().BeNull();
        serializer.Deserialize("test.notification", payload).Should().BeOfType<TestNotification>();
    }

    [Fact]
    public async Task AddCqrsPipelinePack_registers_optional_behaviors_and_executes_requests()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddCqrsPipelinePack(pack =>
        {
            pack.ConfigureRateLimiting = options =>
            {
                options.MaxTokens = 10;
                options.ReplenishRatePerSecond = 10;
                options.Scope = RateLimitScope.Global;
            };

            pack.ConfigureTimeout = options => { options.Timeout = TimeSpan.FromSeconds(5); };
            pack.ConfigureResilience = options => { options.MaxRetries = 1; };
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        var commandResult = await cqrs.Send(new TestCommand());
        commandResult.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void AddCqrsPipelinePack_is_idempotent_and_does_not_stack_duplicate_behaviors()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();

        static void Configure(CqrsPipelinePackOptions pack)
        {
            pack.IncludeLogging = true;
            pack.ConfigureTimeout = options => { options.Timeout = TimeSpan.FromSeconds(5); };
        }

        services.AddCqrsPipelinePack(Configure);
        var afterFirst = services.Count(d => d.ServiceType == typeof(IPipelineBehavior<,>));

        // A second identical call must be a no-op (guarded by the marker) — no behavior registered twice.
        services.AddCqrsPipelinePack(Configure);
        var afterSecond = services.Count(d => d.ServiceType == typeof(IPipelineBehavior<,>));

        afterFirst.Should().BeGreaterThan(0);
        afterSecond.Should().Be(afterFirst);
    }
}