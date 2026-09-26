using System.Collections.Concurrent;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

public sealed class NotificationPipelineTests
{
    [Fact]
    public async Task Publish_executes_notification_pipeline_behaviors_in_order()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddSingleton<NotificationTrace>();

        services.AddTransient<INotificationPipelineBehavior<TestNotification>, BehaviorA>();
        services.AddTransient<INotificationPipelineBehavior<TestNotification>, BehaviorB>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        await cqrs.Publish(new TestNotification(), TestContext.Current.CancellationToken);

        provider.GetRequiredService<NotificationTrace>().Snapshot().Should().Equal(
            "A:before",
            "B:before",
            "handler",
            "B:after",
            "A:after");
    }

    [Fact]
    public async Task Untyped_publish_executes_notification_pipeline_behaviors_in_order()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddSingleton<NotificationTrace>();

        services.AddTransient<INotificationPipelineBehavior<TestNotification>, BehaviorA>();
        services.AddTransient<INotificationPipelineBehavior<TestNotification>, BehaviorB>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        await dispatcher.Publish<INotification>(new TestNotification(), TestContext.Current.CancellationToken);

        provider.GetRequiredService<NotificationTrace>().Snapshot().Should().Equal(
            "A:before",
            "B:before",
            "handler",
            "B:after",
            "A:after");
    }

    [Fact(DisplayName = "A closed behavior for a notification runs when only its base type has a handler")]
    public async Task Closed_behavior_of_the_published_type_wraps_a_base_type_handler()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddSingleton<BaseAuditRecorder>();
        services.AddSingleton<NotificationTrace>();
        services.AddTransient<INotificationPipelineBehavior<DerivedAuditedNotification>, DerivedBehavior>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish(new DerivedAuditedNotification(), TestContext.Current.CancellationToken);

        provider.GetRequiredService<NotificationTrace>().Snapshot().Should().Equal("derived-behavior");
        provider.GetRequiredService<BaseAuditRecorder>().Seen.Should().ContainSingle();
    }

    [Fact(DisplayName = "An open-generic behavior runs once per publish, closed over the runtime type, however the notification was published")]
    public async Task Open_behavior_wraps_the_fan_out_once_as_the_runtime_type()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddSingleton<FanOutRecorder>();
        services.AddSingleton<NotificationTrace>();
        services.AddTransient(typeof(INotificationPipelineBehavior<>), typeof(TypeRecordingBehavior<>));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish<INotification>(new FanOutOrderPlaced(), TestContext.Current.CancellationToken);

        provider.GetRequiredService<NotificationTrace>().Snapshot().Should().Equal(nameof(FanOutOrderPlaced));
        provider.GetRequiredService<FanOutRecorder>().Deliveries.Should().HaveCount(4, "the behavior wraps the whole fan-out, it is not run per handler");
    }

    [Fact(DisplayName = "A behavior runs for a notification nothing handles")]
    public async Task Behavior_runs_without_handlers()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddSingleton<NotificationTrace>();
        services.AddTransient(typeof(INotificationPipelineBehavior<>), typeof(TypeRecordingBehavior<>));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish(new UnhandledNotification(), TestContext.Current.CancellationToken);

        provider.GetRequiredService<NotificationTrace>().Snapshot().Should().Equal(nameof(UnhandledNotification));
    }

    [Fact(DisplayName = "An outbox delivery to a handler declared for an interface runs the behaviors of the notification's type")]
    public async Task Outbox_delivery_runs_the_runtime_type_behaviors()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddSingleton<FanOutRecorder>();
        services.AddSingleton<NotificationTrace>();
        services.AddTransient(typeof(INotificationPipelineBehavior<>), typeof(TypeRecordingBehavior<>));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var registry = provider.GetRequiredService<INotificationSubscriptionRegistry>();

        registry.TryGetSubscription(typeof(FanOutOrderPlaced), "tests.fanout.auditor", out var subscription).Should().BeTrue();
        await provider.GetRequiredService<NotificationPublisher>()
            .Deliver(scope.ServiceProvider, subscription!, new FanOutOrderPlaced(), TestContext.Current.CancellationToken);

        provider.GetRequiredService<NotificationTrace>().Snapshot().Should().Equal(nameof(FanOutOrderPlaced));
        provider.GetRequiredService<FanOutRecorder>().Deliveries.Should().Equal("tests.fanout.auditor");
    }

    internal sealed class NotificationTrace
    {
        private readonly ConcurrentQueue<string> _events = new();

        public void Add(string value) => _events.Enqueue(value);

        public string[] Snapshot() => _events.ToArray();
    }

    internal sealed class RecordingNotificationHandler(NotificationTrace trace) : INotificationHandler<TestNotification>
    {
        public Task Handle(TestNotification notification, CancellationToken cancellationToken)
        {
            trace.Add("handler");
            return Task.CompletedTask;
        }
    }

    internal sealed class BehaviorA(NotificationTrace trace)
        : INotificationPipelineBehavior<TestNotification>, IPrioritizedPipelineBehavior
    {
        public async Task Handle(
            TestNotification notification,
            NotificationHandlerDelegate next,
            CancellationToken cancellationToken)
        {
            trace.Add("A:before");
            await next().ConfigureAwait(false);
            trace.Add("A:after");
        }

        public int PipelineExecutionPriority => 10;
    }

    internal sealed class BehaviorB(NotificationTrace trace)
        : INotificationPipelineBehavior<TestNotification>, IPrioritizedPipelineBehavior
    {
        public async Task Handle(
            TestNotification notification,
            NotificationHandlerDelegate next,
            CancellationToken cancellationToken)
        {
            trace.Add("B:before");
            await next(cancellationToken).ConfigureAwait(false);
            trace.Add("B:after");
        }

        public int PipelineExecutionPriority => 20;
    }

    internal sealed class DerivedBehavior(NotificationTrace trace) : INotificationPipelineBehavior<DerivedAuditedNotification>
    {
        public Task Handle(DerivedAuditedNotification notification, NotificationHandlerDelegate next, CancellationToken cancellationToken)
        {
            trace.Add("derived-behavior");
            return next();
        }
    }

    internal sealed class TypeRecordingBehavior<TNotification>(NotificationTrace trace) : INotificationPipelineBehavior<TNotification>
        where TNotification : INotification
    {
        public Task Handle(TNotification notification, NotificationHandlerDelegate next, CancellationToken cancellationToken)
        {
            // Only the notifications these tests publish: a lifecycle notification of the same scope is not theirs.
            if (notification is FanOutOrderPlaced or UnhandledNotification) trace.Add(typeof(TNotification).Name);
            return next(cancellationToken);
        }
    }
}

/// <summary>A notification declared, and published, with no handler anywhere.</summary>
public sealed record UnhandledNotification : INotification;
