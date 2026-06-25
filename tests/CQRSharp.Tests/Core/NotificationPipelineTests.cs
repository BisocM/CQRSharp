using System.Collections.Concurrent;
using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Core.Extensions;
using CQRSharp.Core.Mediation;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Notifications.Pipelines;
using CQRSharp.Core.Pipelines;
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
        await cqrs.Publish(new TestNotification());

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

        var dispatcher = scope.ServiceProvider.GetRequiredService<IDirectNotificationDispatcher>();
        await dispatcher.Publish((INotification)new TestNotification());

        provider.GetRequiredService<NotificationTrace>().Snapshot().Should().Equal(
            "A:before",
            "B:before",
            "handler",
            "B:after",
            "A:after");
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
        public int PipelineExecutionPriority => 10;

        public async Task Handle(
            TestNotification notification,
            Func<CancellationToken, Task> next,
            CancellationToken cancellationToken)
        {
            trace.Add("A:before");
            await next(cancellationToken).ConfigureAwait(false);
            trace.Add("A:after");
        }
    }

    internal sealed class BehaviorB(NotificationTrace trace)
        : INotificationPipelineBehavior<TestNotification>, IPrioritizedPipelineBehavior
    {
        public int PipelineExecutionPriority => 20;

        public async Task Handle(
            TestNotification notification,
            Func<CancellationToken, Task> next,
            CancellationToken cancellationToken)
        {
            trace.Add("B:before");
            await next(cancellationToken).ConfigureAwait(false);
            trace.Add("B:after");
        }
    }
}
