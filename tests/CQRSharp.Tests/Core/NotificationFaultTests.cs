using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Core.Notifications;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Regression tests for notification fan-out fault handling: a synchronously-throwing handler must not abandon its
///     siblings, and when more than one handler fails every failure must be surfaced (not just the first).
/// </summary>
public class NotificationFaultTests
{
    private sealed record FaultNotification : INotification;

    private sealed class SyncThrowHandler : INotificationHandler<FaultNotification>
    {
        public Task Handle(FaultNotification notification, CancellationToken cancellationToken)
            => throw new InvalidOperationException("A");
    }

    private sealed class AsyncThrowHandler : INotificationHandler<FaultNotification>
    {
        public Task Handle(FaultNotification notification, CancellationToken cancellationToken)
            => Task.FromException(new InvalidOperationException("B"));
    }

    private sealed class FlagHandler : INotificationHandler<FaultNotification>
    {
        public bool Ran { get; private set; }

        public Task Handle(FaultNotification notification, CancellationToken cancellationToken)
        {
            Ran = true;
            return Task.CompletedTask;
        }
    }

    [Fact(DisplayName = "Multiple handler faults are surfaced together as an AggregateException")]
    public async Task Publish_MultipleFaults_SurfacesAll()
    {
        var services = new ServiceCollection();
        services.AddSingleton<INotificationHandler<FaultNotification>, SyncThrowHandler>();
        services.AddSingleton<INotificationHandler<FaultNotification>, AsyncThrowHandler>();
        using var provider = services.BuildServiceProvider();
        var dispatcher = new DirectNotificationDispatcher(provider);

        var act = () => dispatcher.Publish(new FaultNotification(), CancellationToken.None);

        var assertion = await act.Should().ThrowAsync<AggregateException>();
        assertion.Which.InnerExceptions.Select(e => e.Message)
            .Should().BeEquivalentTo("A", "B");
    }

    [Fact(DisplayName = "A synchronously-throwing handler does not abandon sibling handlers")]
    public async Task Publish_SyncThrow_StillRunsSiblings()
    {
        var flag = new FlagHandler();
        var services = new ServiceCollection();
        services.AddSingleton<INotificationHandler<FaultNotification>, SyncThrowHandler>();
        services.AddSingleton<INotificationHandler<FaultNotification>>(flag);
        using var provider = services.BuildServiceProvider();
        var dispatcher = new DirectNotificationDispatcher(provider);

        var act = () => dispatcher.Publish(new FaultNotification(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("A");
        flag.Ran.Should().BeTrue("the sibling handler must run even though an earlier handler threw synchronously");
    }
}
