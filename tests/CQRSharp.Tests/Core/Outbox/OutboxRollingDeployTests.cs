using System.Diagnostics.CodeAnalysis;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Outbox;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Two versions of an application running at once against one outbox store, as during a rolling deploy that adds
///     a handler or a durable notification: an instance still on the previous version claims a message only the newer
///     version can deliver. It must hand the message back for the newer instance, not dead-letter it.
/// </summary>
public sealed class OutboxRollingDeployTests
{
    private const string ExistingHandler = "Tests.RollingDeploy.ExistingHandler";
    private const string AddedHandler = "Tests.RollingDeploy.AddedHandler";
    private const string KnownName = "rolling-deploy.known";
    private const string AddedName = "rolling-deploy.added";

    private static readonly OutboxProcessorOptions ProcessorOptions = new() { PollingInterval = TimeSpan.FromSeconds(5) };

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));

    [Fact(DisplayName = "Outbox rolling deploy: an older instance defers a message for a handler it does not have, at Information, and a newer instance delivers it")]
    public async Task A_message_for_an_added_handler_is_delivered_by_the_instance_that_has_it()
    {
        var store = new ScriptedOutboxStore(_time);
        await store.StoreAsync([NewMessage(KnownName, AddedHandler)], CancellationToken.None);
        var delivered = 0;

        // The older instance knows the notification but not the handler the newer version added.
        var older = new CapturingLogger<OutboxProcessor>();
        await RunOneCycleAsync(store, new Serializer(KnownName), new Registry(ExistingHandler), store.Called("defer"), older);
        older.Entries.Should().ContainSingle(e => e.EventId.Id == 5025).Which.Level.Should().Be(LogLevel.Information, "a deferral is expected while versions differ");
        older.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);

        // The newer instance claims the message once the deferral is over.
        _time.Advance(ProcessorOptions.PollingInterval);
        await RunOneCycleAsync(store, new Serializer(KnownName), new Registry(ExistingHandler, (AddedHandler, () => delivered++)), store.Called("processed"));

        delivered.Should().Be(1, "the instance that has the handler delivered the message");
        (await store.GetDeadLettersAsync(10, CancellationToken.None)).Should().BeEmpty("a message another instance could deliver is never dead-lettered");
        (await store.GetBacklogAsync(CancellationToken.None)).PendingCount.Should().Be(0);
    }

    [Fact(DisplayName = "Outbox rolling deploy: an older instance defers a notification it cannot deserialize, at Information, and a newer instance delivers it")]
    public async Task A_message_of_an_added_notification_is_delivered_by_the_instance_that_knows_it()
    {
        var store = new ScriptedOutboxStore(_time);
        await store.StoreAsync([NewMessage(AddedName, ExistingHandler)], CancellationToken.None);
        var delivered = 0;

        // The older instance's serializer does not know the notification the newer version made durable.
        var older = new CapturingLogger<OutboxProcessor>();
        await RunOneCycleAsync(store, new Serializer(KnownName), new Registry((ExistingHandler, () => delivered++)), store.Called("defer"), older);
        delivered.Should().Be(0);
        older.Entries.Should().ContainSingle(e => e.EventId.Id == 5024).Which.Level.Should().Be(LogLevel.Information, "a deferral is expected while versions differ");
        older.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);

        _time.Advance(ProcessorOptions.PollingInterval);
        await RunOneCycleAsync(store, new Serializer(KnownName, AddedName), new Registry((ExistingHandler, () => delivered++)), store.Called("processed"));

        delivered.Should().Be(1, "the instance that knows the notification delivered it");
        (await store.GetDeadLettersAsync(10, CancellationToken.None)).Should().BeEmpty();
    }

    private OutboxMessage NewMessage(string notificationName, string handlerName)
        => new(Guid.NewGuid(), notificationName, handlerName, [1], _time.GetUtcNow().UtcDateTime, OutboxMessageStatus.Pending, null, null);

    // Runs one instance's processor until the store reports the outcome the step waits for, then stops it before the
    // clock moves, so the two instances never poll at the same time.
    private async Task RunOneCycleAsync(IOutboxStore store, INotificationSerializer serializer, Registry registry, Task outcome, ILogger<OutboxProcessor>? logger = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(serializer)
            .AddSingleton<INotificationSubscriptionRegistry>(registry)
            .AddSingleton(registry.Deliveries)
            .AddTransient<ExistingRollingDeployHandler>()
            .AddTransient<AddedRollingDeployHandler>();
        await using var provider = services.BuildServiceProvider();
        var processor = new OutboxProcessor(
            logger ?? NullLogger<OutboxProcessor>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(ProcessorOptions),
            NotificationPublisher.Create(provider),
            _time);

        await processor.StartAsync(CancellationToken.None);
        try
        {
            await outcome.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }
    }

    private sealed record RollingDeployNotification : INotification;

    // What each handler of this instance does when a message is delivered to it, by the handler's stable name.
    private sealed class Deliveries(IReadOnlyDictionary<string, Action> onDelivery)
    {
        public void Deliver(string handlerName) => onDelivery[handlerName]();
    }

    private sealed class ExistingRollingDeployHandler(Deliveries deliveries) : INotificationHandler<RollingDeployNotification>
    {
        public Task Handle(RollingDeployNotification notification, CancellationToken cancellationToken)
        {
            deliveries.Deliver(ExistingHandler);
            return Task.CompletedTask;
        }
    }

    private sealed class AddedRollingDeployHandler(Deliveries deliveries) : INotificationHandler<RollingDeployNotification>
    {
        public Task Handle(RollingDeployNotification notification, CancellationToken cancellationToken)
        {
            deliveries.Deliver(AddedHandler);
            return Task.CompletedTask;
        }
    }

    // Knows the given names, all of them the one test notification type.
    private sealed class Serializer(params string[] knownNames) : INotificationSerializer
    {
        public bool TryGetNotificationName(Type notificationType, [NotNullWhen(true)] out string? notificationName)
        {
            notificationName = null;
            return false;
        }

        public byte[] Serialize(INotification notification) => [1];

        public INotification? Deserialize(string notificationName, byte[] payload)
            => knownNames.Contains(notificationName) ? new RollingDeployNotification() : null;
    }

    // This instance's handlers by stable name, subscribed to the one notification, and what each does when delivered.
    private sealed class Registry(params (string HandlerName, Action OnDelivery)[] handlers)
        : FakeSubscriptionRegistry(handlers.Select(h => h.HandlerName == AddedHandler
            ? NotificationSubscription.For<AddedRollingDeployHandler, RollingDeployNotification>(h.HandlerName)
            : NotificationSubscription.For<ExistingRollingDeployHandler, RollingDeployNotification>(h.HandlerName)).ToArray())
    {
        public Registry(string handlerName, params (string HandlerName, Action OnDelivery)[] more)
            : this([(handlerName, () => { }), .. more])
        {
        }

        public Deliveries Deliveries { get; } = new(handlers.ToDictionary(h => h.HandlerName, h => h.OnDelivery));
    }
}
