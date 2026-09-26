using System.Collections.Concurrent;
using CQRSharp.Core.Notifications;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static CQRSharp.Tests.Core.OutboxTestHarness;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Per-handler outbox delivery end to end: the generated subscriptions, the in-memory store and the real processor.
///     A durable notification is stored once per subscribed handler, every handler has its own attempts and dead letter,
///     a failing handler never makes a healthy sibling run again, and deliveries that share a partition key reach a
///     handler strictly in order. Time is a <see cref="FakeTimeProvider" /> that moves only to make a retry due
///     (<see cref="OutboxTestHarness.SettleAsync" />).
/// </summary>
public sealed class PerHandlerOutboxDeliveryTests
{
    private static (ServiceProvider Provider, FakeTimeProvider Time, ProbeLog Log) Build(Action<IServiceCollection>? configure = null)
    {
        var (provider, time, _) = BuildProbed(configureServices: services =>
        {
            services.AddSingleton<ProbeLog>();
            configure?.Invoke(services);
        });
        return (provider, time, provider.GetRequiredService<ProbeLog>());
    }

    [Fact(DisplayName = "One publish stores one message per subscribed handler, addressed by the handler's stable name")]
    public async Task A_publish_stores_one_message_per_handler()
    {
        var (provider, _, _) = Build();
        await using var _ = provider;

        await PublishAsync(provider, new PerHandlerProbe(Guid.NewGuid()));

        var stored = Stored(provider);
        stored.Should().HaveCount(2);
        stored.Select(m => m.HandlerName).Should().BeEquivalentTo(
            "tests.failing-probe",
            typeof(HealthyProbeHandler).FullName);
        stored.Select(m => m.NotificationId).Distinct().Should().ContainSingle("both messages come from the same publish");
        stored.Should().OnlyContain(m => m.NotificationType == "tests.per-handler.probe" && m.Status == OutboxMessageStatus.Pending);
    }

    [Fact(DisplayName = "The generated registry knows every handler's subscription, default-named or pinned")]
    public void The_subscription_registry_is_generated()
    {
        var (provider, _, _) = Build();
        using var lifetime = provider;

        var registry = provider.GetRequiredService<INotificationSubscriptionRegistry>();
        var subscriptions = registry.GetSubscriptions(typeof(PerHandlerProbe));

        subscriptions.Select(s => (s.HandlerName, s.HandlerType)).Should().BeEquivalentTo(new[]
        {
            ("tests.failing-probe", typeof(FailingProbeHandler)),
            (typeof(HealthyProbeHandler).FullName!, typeof(HealthyProbeHandler))
        });
        registry.TryGetSubscription(typeof(PerHandlerProbe), "tests.failing-probe", out var pinned).Should().BeTrue();
        pinned!.NotificationType.Should().Be(typeof(PerHandlerProbe));
        registry.TryGetSubscription(typeof(PerHandlerProbe), "no.such.handler", out _).Should().BeFalse();
    }

    [Fact(DisplayName = "A failing handler retries and dead-letters its own message; the healthy sibling runs exactly once")]
    public async Task A_failing_handler_never_makes_its_sibling_run_again()
    {
        var (provider, time, log) = Build();
        await using var _ = provider;
        log.FailingAttemptsBeforeSuccess = int.MaxValue; // always fails: three attempts, then a dead letter
        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await PublishAsync(provider, new PerHandlerProbe(Guid.NewGuid()));

            // Attempt 1: both handlers run once.
            await DrainAsync(provider);
            log.Healthy.Should().HaveCount(1);
            log.Failing.Should().HaveCount(1);

            // Attempts 2 and 3 for the failing handler alone, each when its back-off is over, then its dead letter.
            await SettleAsync(provider, time);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        log.Failing.Should().HaveCount(3, "the failing handler exhausted its attempts");
        log.Healthy.Should().HaveCount(1, "a sibling's failure must not re-run a handler that already succeeded");
        var stored = Stored(provider);
        stored.Should().ContainSingle("the processed message is evicted; only the dead letter remains")
            .Which.Should().Match<OutboxMessage>(m => m.HandlerName == "tests.failing-probe" && m.Status == OutboxMessageStatus.Failed && m.AttemptCount == 3,
                "the exhausting attempt is counted too, so the dead letter tells the whole story");
    }

    [Fact(DisplayName = "Deliveries with the same partition key reach a handler in order, even across a failed attempt")]
    public async Task Partitioned_deliveries_stay_in_order()
    {
        var (provider, time, log) = Build();
        await using var _ = provider;
        var orderA = Guid.NewGuid();
        var orderB = Guid.NewGuid();
        log.FailOrderedOnce = (orderA, 1);

        // Stored before the processor starts: each store wakes a running processor, which could deliver B1 (and evict
        // it from the store) before the snapshot below is taken.
        await PublishAsync(provider,
            new OrderedProbe(orderA, 1),
            new OrderedProbe(orderA, 2),
            new OrderedProbe(orderB, 1));
        Stored(provider).Select(m => m.PartitionKey).Should().BeEquivalentTo(
            orderA.ToString(), orderA.ToString(), orderB.ToString());

        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await SettleAsync(provider, time);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        log.Ordered.Should().HaveCount(4, "A1 failed once, then A1, A2 and B1 were delivered");
        var forA = log.Ordered.Where(e => e.OrderId == orderA).ToList();
        forA.Should().Equal(new[] { (orderA, 1, false), (orderA, 1, true), (orderA, 2, true) },
            "the second delivery for an order must wait until the first one succeeded");
        log.Ordered.Should().Contain((orderB, 1, true));
        log.Ordered.IndexOf((orderB, 1, true)).Should().BeLessThan(log.Ordered.IndexOf((orderA, 2, true)),
            "a different key is never held back by the failing one");
    }

    [Fact(DisplayName = "IPartitionedNotification supplies the key and wins over PartitionBy")]
    public async Task A_computed_partition_key_wins_over_the_declared_property()
    {
        var (provider, _, _) = Build();
        await using var _ = provider;

        await PublishAsync(provider, new ComputedKeyProbe("tenant-1", 7));

        Stored(provider).Should().ContainSingle().Which.PartitionKey.Should().Be("tenant-1/7");
    }

    [Fact(DisplayName = "Notification pipeline behaviors wrap every per-handler delivery")]
    public async Task Behaviors_wrap_each_delivery()
    {
        var (provider, _, log) = Build(services =>
            services.AddTransient(typeof(INotificationPipelineBehavior<>), typeof(CountingNotificationBehavior<>)));
        await using var _ = provider;
        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await PublishAsync(provider, new PerHandlerProbe(Guid.NewGuid()));
            await DrainAsync(provider);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        log.Healthy.Should().HaveCount(1);
        log.Failing.Should().HaveCount(1);
        log.BehaviorRuns.Should().Be(2, "each of the two deliveries is its own pass through the notification pipeline");
    }

    [Fact(DisplayName = "A handler declared for a base and a derived notification type subscribes once per notification, for its nearest declared type")]
    public async Task One_outbox_subscription_per_handler()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        await using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<INotificationSubscriptionRegistry>();

        registry.GetSubscriptions(typeof(SharedDerivedEvent)).Where(s => s.HandlerType == typeof(BothLevelsHandler))
            .Should().ContainSingle().Which.NotificationType.Should().Be(typeof(SharedDerivedEvent));
        registry.GetSubscriptions(typeof(SharedBaseEvent)).Where(s => s.HandlerType == typeof(BothLevelsHandler))
            .Should().ContainSingle().Which.NotificationType.Should().Be(typeof(SharedBaseEvent));

        var name = registry.Subscriptions.First(s => s.HandlerType == typeof(BothLevelsHandler)).HandlerName;
        registry.TryGetSubscription(typeof(SharedDerivedEvent), name, out var subscription).Should().BeTrue();
        subscription!.NotificationType.Should().Be(typeof(SharedDerivedEvent));
    }
}

public sealed class ProbeLog
{
    public ConcurrentQueue<Guid> Healthy { get; } = new();
    public ConcurrentQueue<Guid> Failing { get; } = new();
    public List<(Guid OrderId, int Seq, bool Succeeded)> Ordered { get; } = [];
    public int FailingAttemptsBeforeSuccess { get; set; }
    public (Guid OrderId, int Seq)? FailOrderedOnce { get; set; }
    public int BehaviorRuns;
}

[NotificationName("tests.per-handler.probe")]
public sealed record PerHandlerProbe(Guid Id) : INotification;

public sealed class HealthyProbeHandler(ProbeLog log) : INotificationHandler<PerHandlerProbe>
{
    public Task Handle(PerHandlerProbe notification, CancellationToken cancellationToken)
    {
        log.Healthy.Enqueue(notification.Id);
        return Task.CompletedTask;
    }
}

[NotificationHandlerName("tests.failing-probe")]
public sealed class FailingProbeHandler(ProbeLog log) : INotificationHandler<PerHandlerProbe>
{
    public Task Handle(PerHandlerProbe notification, CancellationToken cancellationToken)
    {
        log.Failing.Enqueue(notification.Id);
        return log.Failing.Count <= log.FailingAttemptsBeforeSuccess
            ? throw new InvalidOperationException("probe handler failure")
            : Task.CompletedTask;
    }
}

[NotificationName("tests.ordered.probe", PartitionBy = nameof(OrderId))]
public sealed record OrderedProbe(Guid OrderId, int Seq) : INotification;

public sealed class OrderedProbeHandler(ProbeLog log) : INotificationHandler<OrderedProbe>
{
    public Task Handle(OrderedProbe notification, CancellationToken cancellationToken)
    {
        lock (log.Ordered)
        {
            if (log.FailOrderedOnce is { } once && once.OrderId == notification.OrderId && once.Seq == notification.Seq)
            {
                log.FailOrderedOnce = null;
                log.Ordered.Add((notification.OrderId, notification.Seq, false));
                throw new InvalidOperationException("first delivery fails");
            }

            log.Ordered.Add((notification.OrderId, notification.Seq, true));
        }

        return Task.CompletedTask;
    }
}

[NotificationName("tests.computed-key.probe", PartitionBy = nameof(Number))]
public sealed record ComputedKeyProbe(string Tenant, int Number) : INotification, IPartitionedNotification
{
    public string? PartitionKey => $"{Tenant}/{Number}";
}

public sealed class ComputedKeyProbeHandler : INotificationHandler<ComputedKeyProbe>
{
    public Task Handle(ComputedKeyProbe notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class CountingNotificationBehavior<TNotification>(ProbeLog log) : INotificationPipelineBehavior<TNotification>
    where TNotification : INotification
{
    public Task Handle(TNotification notification, NotificationHandlerDelegate next, CancellationToken cancellationToken)
    {
        if (notification is PerHandlerProbe) Interlocked.Increment(ref log.BehaviorRuns);
        return next(cancellationToken);
    }
}
