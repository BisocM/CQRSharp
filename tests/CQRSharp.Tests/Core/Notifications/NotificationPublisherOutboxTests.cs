using CQRSharp.Core.Notifications;
using CQRSharp.Core.Outbox;
using CQRSharp.Persistence;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Where <see cref="NotificationPublisher.Publish{TNotification}" /> sends a notification: into the outbox when the
///     outbox mode routes it there and the serializer names it, otherwise in-process to its handlers. Which handlers an
///     in-process publish reaches is <see cref="NotificationFanOutTests" />' subject.
/// </summary>
public sealed class NotificationPublisherOutboxTests
{
    private readonly InProcessDeliveries _inProcess = new();
    private readonly TestNotification _testNotification = new();
    private readonly UnstableTestNotification _unstableNotification = new();
    private readonly TransactionLog _log = new();

    private (ServiceProvider Provider, RecordingOutboxStore Store, RecordingUnitOfWork UnitOfWork) Build(OutboxMode mode, bool registerOutbox = true)
    {
        var services = new ServiceCollection();
        services.Configure<OutboxOptions>(o => o.Mode = mode);
        services.AddSingleton(_inProcess);
        services.AddTransient<SubscribedHandler>();
        services.AddTransient<INotificationHandler<UnstableTestNotification>, HandRegisteredHandler>();
        services.AddSingleton<INotificationSerializer>(new SingleTypeNotificationSerializer<TestNotification>("test.notification"));
        services.AddSingleton<INotificationSubscriptionRegistry>(
            new FakeSubscriptionRegistry(NotificationSubscription.For<SubscribedHandler, TestNotification>("Tests.Handler")));
        services.AddSingleton(NotificationPublisher.Create);

        var unitOfWork = new RecordingUnitOfWork(_log);
        services.AddScoped<IUnitOfWork>(_ => unitOfWork);
        var store = new RecordingOutboxStore(joinsUnitOfWork: false, _log);
        services.AddSingleton<IOutboxStore>(store);
        if (registerOutbox) services.AddScoped<ScopedOutbox>();

        return (services.BuildServiceProvider(), store, unitOfWork);
    }

    private static Task Publish<TNotification>(IServiceProvider scope, TNotification notification) where TNotification : INotification
        => scope.GetRequiredService<NotificationPublisher>().Publish(scope, notification, CancellationToken.None);

    // As the executor runs a request: registered with the scope's buffer and current for everything the body awaits.
    private static async Task AsRequest(IServiceProvider scope, Func<Task> body)
    {
        scope.GetRequiredService<ScopedOutbox>().BeginRequest();
        await body();
    }

    [Fact(DisplayName = "With the outbox disabled a notification is dispatched in-process")]
    public async Task Publish_WhenOutboxIsDisabled_DispatchesDirectly()
    {
        var (provider, store, _) = Build(OutboxMode.Disabled);
        await using var _ = provider;
        await using var scope = provider.CreateAsyncScope();

        await AsRequest(scope.ServiceProvider, () => Publish(scope.ServiceProvider, _testNotification));

        _inProcess.Published.Should().ContainSingle().Which.Should().BeSameAs(_testNotification);
        scope.ServiceProvider.GetRequiredService<ScopedOutbox>().Count.Should().Be(0);
        store.Stored.Should().BeEmpty();
    }

    [Fact(DisplayName = "Enabled: a publish inside a running request of the scope is buffered for that request")]
    public async Task Publish_WhenOutboxIsEnabled_InsideARequest_Buffers()
    {
        var (provider, store, _) = Build(OutboxMode.Enabled);
        await using var _ = provider;
        await using var scope = provider.CreateAsyncScope();

        await AsRequest(scope.ServiceProvider, () => Publish(scope.ServiceProvider, _testNotification));

        scope.ServiceProvider.GetRequiredService<ScopedOutbox>().Count.Should().Be(1);
        store.Stored.Should().BeEmpty("the request stores it when it succeeds");
        _inProcess.Published.Should().BeEmpty();
    }

    [Fact(DisplayName = "Enabled: a publish outside any request goes straight to the store, even while another request of the scope runs")]
    public async Task Publish_WhenOutboxIsEnabled_OutsideARequest_Stores()
    {
        var (provider, store, _) = Build(OutboxMode.Enabled);
        await using var _ = provider;
        await using var scope = provider.CreateAsyncScope();
        var running = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var sibling = AsRequest(scope.ServiceProvider, async () =>
        {
            running.SetResult();
            await release.Task;
        });
        await running.Task;

        await Publish(scope.ServiceProvider, _testNotification);

        store.Stored.Should().ContainSingle();
        scope.ServiceProvider.GetRequiredService<ScopedOutbox>().Count.Should().Be(0);
        release.SetResult();
        await sibling;
    }

    [Fact(DisplayName = "Enabled: without the scoped buffer registered, a publish goes straight to the store")]
    public async Task Publish_WhenOutboxIsEnabled_WithoutTheBuffer_Stores()
    {
        var (provider, store, _) = Build(OutboxMode.Enabled, registerOutbox: false);
        await using var _ = provider;
        await using var scope = provider.CreateAsyncScope();

        await Publish(scope.ServiceProvider, _testNotification);

        store.Stored.Should().ContainSingle();
    }

    [Fact(DisplayName = "Transactional: without an active transaction a notification is dispatched in-process")]
    public async Task Publish_WhenModeIsTransactional_And_NoTransaction_DispatchesDirectly()
    {
        var (provider, store, _) = Build(OutboxMode.Transactional);
        await using var _ = provider;
        await using var scope = provider.CreateAsyncScope();

        await AsRequest(scope.ServiceProvider, () => Publish(scope.ServiceProvider, _testNotification));

        _inProcess.Published.Should().ContainSingle().Which.Should().BeSameAs(_testNotification);
        scope.ServiceProvider.GetRequiredService<ScopedOutbox>().Count.Should().Be(0);
        store.Stored.Should().BeEmpty();
    }

    [Fact(DisplayName = "Transactional: with an active transaction a publish inside a request is buffered")]
    public async Task Publish_WhenModeIsTransactional_And_HasTransaction_Buffers()
    {
        var (provider, _, unitOfWork) = Build(OutboxMode.Transactional);
        await using var _ = provider;
        await using var scope = provider.CreateAsyncScope();
        unitOfWork.HasActiveTransaction = true;

        await AsRequest(scope.ServiceProvider, () => Publish(scope.ServiceProvider, _testNotification));

        scope.ServiceProvider.GetRequiredService<ScopedOutbox>().Count.Should().Be(1);
        _inProcess.Published.Should().BeEmpty();
    }

    [Fact(DisplayName = "A publish that goes straight to the store fails clearly when no store is registered")]
    public async Task Publish_WithoutAStore_Throws()
    {
        var services = new ServiceCollection();
        services.Configure<OutboxOptions>(o => o.Mode = OutboxMode.Enabled);
        services.AddSingleton<INotificationSerializer>(new SingleTypeNotificationSerializer<TestNotification>("test.notification"));
        services.AddSingleton(NotificationPublisher.Create);
        await using var provider = services.BuildServiceProvider();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Publish(provider, _testNotification));
        Assert.Contains("IOutboxStore", ex.Message);
    }

    [Fact(DisplayName = "With the outbox enabled, a notification the serializer does not name is dispatched in-process")]
    public async Task Publish_WhenOutboxIsEnabled_ButNotificationHasNoStableName_DispatchesDirectly()
    {
        var (provider, store, _) = Build(OutboxMode.Enabled);
        await using var _ = provider;
        await using var scope = provider.CreateAsyncScope();

        await AsRequest(scope.ServiceProvider, () => Publish(scope.ServiceProvider, _unstableNotification));

        _inProcess.Published.Should().ContainSingle().Which.Should().BeSameAs(_unstableNotification);
        scope.ServiceProvider.GetRequiredService<ScopedOutbox>().Count.Should().Be(0);
        store.Stored.Should().BeEmpty();
    }

    private sealed record UnstableTestNotification : INotification;

    private sealed class InProcessDeliveries
    {
        private readonly List<INotification> _published = [];

        public IReadOnlyList<INotification> Published
        {
            get
            {
                lock (_published) return _published.ToArray();
            }
        }

        public Task Add(INotification notification)
        {
            lock (_published) _published.Add(notification);
            return Task.CompletedTask;
        }
    }

    private sealed class SubscribedHandler(InProcessDeliveries deliveries) : INotificationHandler<TestNotification>
    {
        public Task Handle(TestNotification notification, CancellationToken cancellationToken) => deliveries.Add(notification);
    }

    private sealed class HandRegisteredHandler(InProcessDeliveries deliveries) : INotificationHandler<UnstableTestNotification>
    {
        public Task Handle(UnstableTestNotification notification, CancellationToken cancellationToken) => deliveries.Add(notification);
    }
}
