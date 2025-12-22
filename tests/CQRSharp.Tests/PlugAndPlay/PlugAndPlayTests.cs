using CQRSharp.Abstractions.Data.Interfaces.Notifications;
using CQRSharp.Core.Extensions;
using CQRSharp.Core.Pipelines;
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
        var dispatcher = provider.GetRequiredService<IRequestDispatcher>();

        var commandResult = await dispatcher.ExecuteAsync(new TestCommand());
        commandResult.IsSuccess.Should().BeTrue();

        var queryResult = await dispatcher.ExecuteAsync(new TestQuery());
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
}

