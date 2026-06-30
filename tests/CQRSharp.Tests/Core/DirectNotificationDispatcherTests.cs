using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Core.Notifications;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

public class DirectNotificationDispatcherTests
{
    [Fact]
    public async Task Publish_Untyped_ThrowsWithoutGeneratedDispatcher()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDirectNotificationDispatcher, DirectNotificationDispatcher>();

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDirectNotificationDispatcher>();

        INotification notification = new TestNotification();
        var act = () => dispatcher.Publish(notification, CancellationToken.None);
        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*AddCqrsGenerated*");
    }
}