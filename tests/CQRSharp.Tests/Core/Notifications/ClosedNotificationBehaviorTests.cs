using CQRSharp.Core.Notifications;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Closed behavior resolution for notifications: without dynamic code the container cannot close an open-generic
///     notification behavior over a value-type notification, so a publish of one, in-process or as an outbox delivery,
///     resolves its behaviors from the closed set the generated factories build. These tests switch that resolution on,
///     as Native AOT does; a behavior generated code could not close is refused rather than silently left out, and one
///     whose constraints exclude the notification is skipped.
/// </summary>
public sealed class ClosedNotificationBehaviorTests
{
    [Fact(DisplayName = "With closed behavior resolution, a value-type notification runs its open-generic behaviors, published as itself or as INotification")]
    public async Task Value_type_notifications_run_their_behaviors_through_the_closed_set()
    {
        await using var provider = Build(typeof(RecordingNotificationBehavior<>));
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var probe = provider.GetRequiredService<NotificationBehaviorProbe>();

        await dispatcher.Publish(new MeterReading(1), TestContext.Current.CancellationToken);
        await dispatcher.Publish<INotification>(new MeterReading(2), TestContext.Current.CancellationToken);

        probe.Entries.Should().Equal("behavior:MeterReading", "handler:1", "behavior:MeterReading", "handler:2");
    }

    [Fact(DisplayName = "With closed behavior resolution, an outbox delivery of a value-type notification runs its open-generic behaviors around its handler")]
    public async Task Value_type_deliveries_run_their_behaviors_through_the_closed_set()
    {
        await using var provider = Build(typeof(RecordingNotificationBehavior<>));
        await using var scope = provider.CreateAsyncScope();
        var subscriptions = provider.GetRequiredService<INotificationSubscriptionRegistry>();
        var subscription = subscriptions.GetSubscriptions(typeof(MeterReading)).Should().ContainSingle().Subject;

        await subscription.Invoke(scope.ServiceProvider, new MeterReading(3), TestContext.Current.CancellationToken);

        provider.GetRequiredService<NotificationBehaviorProbe>().Entries.Should().Equal("behavior:MeterReading", "handler:3");
    }

    // UnclosableNotificationBehavior is private, so the generated code of this assembly cannot close it over MeterReading
    // and records the gap instead.
    [Fact(DisplayName = "With closed behavior resolution, a registered notification behavior generated code could not close fails the publish instead of being left out")]
    public async Task A_notification_behavior_that_could_not_be_closed_is_refused()
    {
        await using var provider = Build(typeof(UnclosableNotificationBehavior<>));
        await using var scope = provider.CreateAsyncScope();

        var publish = () => scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish(new MeterReading(4), TestContext.Current.CancellationToken);

        (await publish.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain(typeof(UnclosableNotificationBehavior<>).FullName!).And.Contain(typeof(MeterReading).FullName!);
        provider.GetRequiredService<NotificationBehaviorProbe>().Entries.Should().BeEmpty("the notification is not delivered without a behavior that applies to it");
    }

    [Fact(DisplayName = "With closed behavior resolution, a notification behavior whose constraints exclude a value type needs no factory for it and is skipped")]
    public async Task A_notification_behavior_that_does_not_apply_is_skipped()
    {
        await using var provider = Build(typeof(ReferenceNotificationBehavior<>));
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish(new MeterReading(5), TestContext.Current.CancellationToken);

        provider.GetRequiredService<NotificationBehaviorProbe>().Entries.Should().Equal("handler:5");
    }

    private static ServiceProvider Build(Type openBehavior)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ClosedBehaviorResolution { Enabled = true });
        services.AddSingleton<NotificationBehaviorProbe>();
        services.AddCqrsGenerated();
        services.AddTransient(typeof(INotificationPipelineBehavior<>), openBehavior);
        return services.BuildServiceProvider();
    }

    private sealed class UnclosableNotificationBehavior<TNotification> : INotificationPipelineBehavior<TNotification>
        where TNotification : INotification
    {
        public Task Handle(TNotification notification, NotificationHandlerDelegate next, CancellationToken cancellationToken) => next(cancellationToken);
    }
}

public sealed class NotificationBehaviorProbe
{
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_entries) return _entries.ToArray();
        }
    }

    public void Add(string entry)
    {
        lock (_entries) _entries.Add(entry);
    }
}

/// <summary>A notification that is a value type, the shape Native AOT cannot close an open-generic behavior over.</summary>
public readonly struct MeterReading(int value) : INotification
{
    public int Value { get; } = value;
}

public sealed class MeterReadingHandler(NotificationBehaviorProbe probe) : INotificationHandler<MeterReading>
{
    public Task Handle(MeterReading notification, CancellationToken cancellationToken)
    {
        probe.Add($"handler:{notification.Value}");
        return Task.CompletedTask;
    }
}

public sealed class RecordingNotificationBehavior<TNotification>(NotificationBehaviorProbe probe) : INotificationPipelineBehavior<TNotification>
    where TNotification : INotification
{
    public Task Handle(TNotification notification, NotificationHandlerDelegate next, CancellationToken cancellationToken)
    {
        probe.Add($"behavior:{typeof(TNotification).Name}");
        return next(cancellationToken);
    }
}

public sealed class ReferenceNotificationBehavior<TNotification>(NotificationBehaviorProbe probe) : INotificationPipelineBehavior<TNotification>
    where TNotification : class, INotification
{
    public Task Handle(TNotification notification, NotificationHandlerDelegate next, CancellationToken cancellationToken)
    {
        probe.Add($"behavior:{typeof(TNotification).Name}");
        return next(cancellationToken);
    }
}
