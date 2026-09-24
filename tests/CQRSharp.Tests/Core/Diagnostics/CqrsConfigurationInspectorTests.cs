using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Notifications;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Unit tests for <see cref="CqrsConfigurationInspector.Inspect" />, driving it with a hand-built
///     <see cref="ServiceCollection" /> (or hand-built modules, composed as the bootstrap composes generated ones) to
///     exercise each CQRCONF code in isolation.
/// </summary>
public sealed class CqrsConfigurationInspectorTests
{
    private static IReadOnlyList<CqrsBindingIssue> Inspect(
        IServiceProvider services,
        OutboxMode mode,
        IReadOnlyList<CqrsRequestBinding>? requestBindings = null)
        => CqrsConfigurationInspector.Inspect(
            services,
            new OutboxOptions { Mode = mode },
            requestBindings ?? Array.Empty<CqrsRequestBinding>());

    // A composed application with one module: routes for both notification types, and a handler for each.
    private static ServiceProvider WithNotifications()
    {
        var module = new TestModule
        {
            NotificationRoutes = new Dictionary<Type, NotificationRoute>
            {
                [typeof(StableNamed)] = NotificationRoute.For<StableNamed>(),
                [typeof(UnstableNamed)] = NotificationRoute.For<UnstableNamed>()
            },
            NotificationSubscriptions =
            [
                NotificationSubscription.For<HandlerA, StableNamed>("handler.a"),
                NotificationSubscription.For<HandlerB, UnstableNamed>("handler.b")
            ]
        };
        var services = TestModule.Compose(new ServiceCollection(), module);
        services.AddSingleton<IOutboxStore, NullOutboxStore>();
        services.AddNotificationSerializer<FakeNotificationSerializer>();
        return services.BuildServiceProvider();
    }

    private static CqrsRequestBinding BindingFor(
        Type requestType,
        CqrsPipelineBehaviorBinding? pipeline = null,
        CqrsPipelineBehaviorBinding? exempted = null,
        params CqrsBindingIssue[] issues)
        => new(
            requestType,
            typeof(object),
            null,
            null,
            Array.Empty<Type>(),
            Array.Empty<CqrsInterceptorBinding>(),
            Array.Empty<CqrsInterceptorBinding>(),
            pipeline is null ? Array.Empty<CqrsPipelineBehaviorBinding>() : new[] { pipeline },
            exempted is null ? Array.Empty<CqrsPipelineBehaviorBinding>() : new[] { exempted },
            issues);

    [Fact]
    public void CQRCONF001_reported_when_outbox_enabled_but_services_missing()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        var issues = Inspect(provider, OutboxMode.Enabled);

        var issue = issues.Should().ContainSingle(i => i.Code == "CQRCONF001").Subject;
        issue.Severity.Should().Be(CqrsBindingIssueSeverity.Error);
        issue.Message.Should().Contain(nameof(IOutboxStore));
        issue.Message.Should().Contain(nameof(INotificationSerializer));
        issue.Message.Should().Contain(nameof(INotificationSubscriptionRegistry));
        issue.Message.Should().Contain("UseOutbox");
    }

    [Fact]
    public void CQRCONF001_lists_only_the_missing_services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore, NullOutboxStore>();
        using var provider = services.BuildServiceProvider();

        var issue = Inspect(provider, OutboxMode.Enabled).Should().ContainSingle(i => i.Code == "CQRCONF001").Subject;

        issue.Message.Should().NotContain(nameof(IOutboxStore) + ",");
        issue.Message.Should().Contain(nameof(INotificationSerializer));
        issue.Message.Should().Contain(nameof(INotificationSubscriptionRegistry));
    }

    [Fact]
    public void CQRCONF001_cleared_when_all_outbox_services_present()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore, NullOutboxStore>();
        services.AddSingleton<INotificationSerializer, FakeNotificationSerializer>();
        services.AddSingleton<INotificationSubscriptionRegistry, FakeSubscriptionRegistry>();
        using var provider = services.BuildServiceProvider();

        Inspect(provider, OutboxMode.Enabled).Should().NotContain(i => i.Code == "CQRCONF001");
    }

    [Fact]
    public void CQRCONF001_not_reported_when_outbox_disabled()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        Inspect(provider, OutboxMode.Disabled).Should().BeEmpty();
    }

    [Fact(DisplayName = "CQRCONF003 warns once per notification with handlers that the registered serializer does not name")]
    public async Task CQRCONF003_warns_per_handled_notification_without_stable_name()
    {
        await using var provider = WithNotifications();
        await using var scope = provider.CreateAsyncScope();

        var conf003 = Inspect(scope.ServiceProvider, OutboxMode.Enabled).Where(i => i.Code == "CQRCONF003").ToArray();

        conf003.Should().ContainSingle();
        conf003[0].Severity.Should().Be(CqrsBindingIssueSeverity.Warning);
        conf003[0].Message.Should().Contain(typeof(UnstableNamed).FullName!);
    }

    [Fact(DisplayName = "CQRCONF003 under a custom serializer points at that serializer, not at [NotificationName]")]
    public async Task CQRCONF003_names_the_custom_serializer_as_the_remedy()
    {
        await using var provider = WithNotifications();
        await using var scope = provider.CreateAsyncScope();

        var issue = Inspect(scope.ServiceProvider, OutboxMode.Enabled).Should().ContainSingle(i => i.Code == "CQRCONF003").Subject;

        issue.Message.Should().Contain(typeof(FakeNotificationSerializer).FullName!).And.Contain("TryGetNotificationName");
        issue.Message.Should().NotContain("Add [NotificationName]", "the generated serializer is not in use, so the attribute alone changes nothing");
    }

    [Fact(DisplayName = "CQRCONF003 under the generated serializer reports the notifications with handlers but without [NotificationName]")]
    public void CQRCONF003_under_the_generated_serializer_asks_for_NotificationName()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.Enabled().UseInMemoryStore()));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var conf003 = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>().DescribeConfiguration()
            .Where(i => i.Code == "CQRCONF003")
            .ToArray();

        conf003.Should().NotContain(i => i.Message.Contains(typeof(CQRSharp.Tests.Shared.TestNotification).FullName + "'"),
            "the generated serializer names a [NotificationName] notification");
        conf003.Should().ContainSingle(i => i.Message.Contains(typeof(CQRSharp.Tests.ExternalModule.ExternalNotification).FullName + "'"))
            .Which.Message.Should().Contain("Add [NotificationName]");
        conf003.Should().ContainSingle(i => i.Message.Contains(typeof(DerivedAuditedNotification).FullName + "'"),
            "a notification only a base type's handler handles is still delivered in-process");
        conf003.Should().NotContain(i => i.Message.Contains(typeof(MeterReading).FullName + "'"),
            "a value-type notification cannot carry [NotificationName], so its in-process delivery is no omission");
    }

    [Fact(DisplayName = "CQRCONF003 under the generated serializer leaves CQRSharp's own lifecycle notifications alone: an application handles them but cannot name them")]
    public async Task CQRCONF003_skips_framework_notifications_under_the_generated_serializer()
    {
        var module = new TestModule
        {
            NotificationRoutes = new Dictionary<Type, NotificationRoute>
            {
                [typeof(CommandCompletedNotification)] = NotificationRoute.For<CommandCompletedNotification>(),
                [typeof(QueryCompletedNotification<int>)] = NotificationRoute.For<QueryCompletedNotification<int>>(),
                [typeof(UnstableNamed)] = NotificationRoute.For<UnstableNamed>()
            },
            NotificationSubscriptions =
            [
                NotificationSubscription.For<CompletedAudit, CommandCompletedNotification>("audit.command"),
                NotificationSubscription.For<QueryAudit, QueryCompletedNotification<int>>("audit.query"),
                NotificationSubscription.For<HandlerB, UnstableNamed>("handler.b")
            ]
        };
        var services = TestModule.Compose(new ServiceCollection(), module);
        services.AddSingleton<IOutboxStore, NullOutboxStore>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var conf003 = Inspect(scope.ServiceProvider, OutboxMode.Enabled).Where(i => i.Code == "CQRCONF003").ToArray();

        conf003.Should().ContainSingle().Which.Message.Should().Contain(typeof(UnstableNamed).FullName!, "an application notification is still reported");
    }

    [Fact(DisplayName = "CQRCONF003 stays quiet without a serializer: CQRCONF001 already reports the missing one")]
    public void CQRCONF003_not_reported_without_a_serializer()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        var issues = Inspect(provider, OutboxMode.Enabled);

        issues.Should().Contain(i => i.Code == "CQRCONF001").And.NotContain(i => i.Code == "CQRCONF003");
    }

    [Fact]
    public async Task CQRCONF003_not_reported_when_outbox_disabled()
    {
        await using var provider = WithNotifications();
        await using var scope = provider.CreateAsyncScope();

        Inspect(scope.ServiceProvider, OutboxMode.Disabled).Should().BeEmpty();
    }

    [Fact(DisplayName = "CQRCONF005 is an error: an idempotent request without the idempotency behavior processes every duplicate")]
    public void CQRCONF005_errors_when_idempotent_request_has_no_idempotency_behavior()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        var issue = Inspect(provider, OutboxMode.Disabled, requestBindings: new[] { BindingFor(typeof(IdempotentRequest)) })
            .Should().ContainSingle(i => i.Code == "CQRCONF005").Subject;

        issue.Severity.Should().Be(CqrsBindingIssueSeverity.Error);
        issue.Message.Should().Contain(typeof(IdempotentRequest).FullName!);
        issue.Message.Should().Contain("UseIdempotency");
    }

    [Fact]
    public void CQRCONF005_cleared_when_idempotency_behavior_is_wired()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        var binding = BindingFor(
            typeof(IdempotentRequest),
            new CqrsPipelineBehaviorBinding(typeof(IdempotencyBehavior<IdempotentRequest, object>), 0));

        Inspect(provider, OutboxMode.Disabled, requestBindings: new[] { binding })
            .Should().NotContain(i => i.Code == "CQRCONF005");
    }

    [Fact]
    public void CQRCONF005_cleared_when_idempotency_behavior_is_registered_but_exempted()
    {
        // The behavior is registered but the request opts out via [PipelineExemption] — an explicit choice, not an
        // oversight, so the inspector must not nag.
        using var provider = new ServiceCollection().BuildServiceProvider();

        var binding = BindingFor(
            typeof(IdempotentRequest),
            exempted: new CqrsPipelineBehaviorBinding(typeof(IdempotencyBehavior<IdempotentRequest, object>), 0));

        Inspect(provider, OutboxMode.Disabled, requestBindings: new[] { binding })
            .Should().NotContain(i => i.Code == "CQRCONF005");
    }

    [Fact]
    public void CQRCONF006_warns_when_retryable_request_has_no_resilience_behavior()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        var issue = Inspect(provider, OutboxMode.Disabled, requestBindings: new[] { BindingFor(typeof(RetryableRequest)) })
            .Should().ContainSingle(i => i.Code == "CQRCONF006").Subject;

        issue.Severity.Should().Be(CqrsBindingIssueSeverity.Warning);
        issue.Message.Should().Contain(typeof(RetryableRequest).FullName!);
        issue.Message.Should().Contain("UseResilience");
    }

    [Fact]
    public void CQRCONF006_cleared_when_resilience_behavior_is_wired()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        var binding = BindingFor(
            typeof(RetryableRequest),
            new CqrsPipelineBehaviorBinding(typeof(ResilienceBehavior<RetryableRequest, object>), 0));

        Inspect(provider, OutboxMode.Disabled, requestBindings: new[] { binding })
            .Should().NotContain(i => i.Code == "CQRCONF006");
    }

    [Fact(DisplayName = "CQRCONF005 and CQRCONF006 are not concluded for a request whose behaviors could not be resolved (CQRDIAG004)")]
    public void CQRCONF005_and_006_not_reported_when_the_pipeline_is_unknown()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var unresolved = new CqrsBindingIssue(CqrsBindingIssueSeverity.Error, "CQRDIAG004", "Failed to resolve pipeline behaviors");

        var issues = Inspect(provider, OutboxMode.Disabled, requestBindings:
        [
            BindingFor(typeof(IdempotentRequest), issues: unresolved),
            BindingFor(typeof(RetryableRequest), issues: unresolved)
        ]);

        issues.Should().NotContain(i => i.Code == "CQRCONF005" || i.Code == "CQRCONF006");
    }

    [Fact]
    public void CQRCONF005_and_006_not_reported_for_a_request_without_the_markers()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        var issues = Inspect(provider, OutboxMode.Disabled, requestBindings: new[] { BindingFor(typeof(object)) });

        issues.Should().NotContain(i => i.Code == "CQRCONF005" || i.Code == "CQRCONF006");
    }

    [Fact(DisplayName = "CQRCONF007 is an error: a transactional outbox requires a unit of work")]
    public async Task CQRCONF007_errors_when_transactional_without_any_unit_of_work()
    {
        var services = new ServiceCollection();
        // Supply the outbox triplet so only CQRCONF007 is in play; register NO IUnitOfWork.
        services.AddSingleton<IOutboxStore, NullOutboxStore>();
        services.AddSingleton<INotificationSerializer, FakeNotificationSerializer>();
        services.AddSingleton<INotificationSubscriptionRegistry, FakeSubscriptionRegistry>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var issue = Inspect(scope.ServiceProvider, OutboxMode.Transactional)
            .Should().ContainSingle(i => i.Code == "CQRCONF007").Subject;

        issue.Severity.Should().Be(CqrsBindingIssueSeverity.Error);
        issue.Message.Should().Contain("no IUnitOfWork is registered")
            .And.Contain("UseUnitOfWork(...)")
            .And.Contain("UseEntityFrameworkCoreUnitOfWork<TContext>()")
            .And.Contain("Enabled mode");
    }

    [Fact]
    public async Task CQRCONF007_not_reported_when_a_unit_of_work_is_registered()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore, NullOutboxStore>();
        services.AddSingleton<INotificationSerializer, FakeNotificationSerializer>();
        services.AddSingleton<INotificationSubscriptionRegistry, FakeSubscriptionRegistry>();
        services.AddScoped<IUnitOfWork, RecordingUnitOfWork>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        Inspect(scope.ServiceProvider, OutboxMode.Transactional).Should().NotContain(i => i.Code == "CQRCONF007");
    }

    [Fact(DisplayName = "CQRCONF011 warns about a handler registered by hand for a durable notification: the outbox never reaches it")]
    public async Task CQRCONF011_warns_about_hand_registered_handlers_of_durable_notifications()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.Enabled().UseInMemoryStore()));
        services.AddSingleton<FanOutRecorder>();
        services.AddTransient<INotificationHandler<FanOutOrderPlaced>, HandRegisteredOrderPlacedHandler>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var issue = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>().DescribeConfiguration()
            .Should().ContainSingle(i => i.Code == "CQRCONF011").Subject;

        issue.Severity.Should().Be(CqrsBindingIssueSeverity.Warning);
        issue.Message.Should().Contain(typeof(FanOutOrderPlaced).FullName!).And.Contain(typeof(HandRegisteredOrderPlacedHandler).FullName!);
    }

    [Fact(DisplayName = "CQRCONF011 stays quiet for a generated handler that is also registered by hand: its subscription reaches it")]
    public async Task CQRCONF011_not_reported_for_a_subscribed_handler_registered_by_hand()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.Enabled().UseInMemoryStore()));
        services.AddSingleton<FanOutRecorder>();
        services.AddTransient<INotificationHandler<FanOutOrderPlaced>, FanOutOwnHandler>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>().DescribeConfiguration().Should().NotContain(i => i.Code == "CQRCONF011");
    }

    [Fact(DisplayName = "The generated serializer names the [NotificationName] notifications, and nothing else")]
    public void Generated_serializer_names_stable_notifications()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        using var provider = services.BuildServiceProvider();

        var serializer = provider.GetRequiredService<INotificationSerializer>();

        serializer.TryGetNotificationName(typeof(CQRSharp.Tests.Shared.TestNotification), out var name).Should().BeTrue();
        name.Should().Be("test.notification");
        serializer.TryGetNotificationName(typeof(object), out _).Should().BeFalse();
    }

    [Fact]
    public void DescribeConfiguration_reports_CQRCONF001_when_outbox_enabled_without_store()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.Configure<OutboxOptions>(o => o.Mode = OutboxMode.Enabled);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var diagnostics = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>();

        diagnostics.DescribeConfiguration().Should().Contain(i => i.Code == "CQRCONF001");
    }

    // Private: a handler the generator could see would be subscribed, not registered by hand.
    private sealed class HandRegisteredOrderPlacedHandler : INotificationHandler<FanOutOrderPlaced>
    {
        public Task Handle(FanOutOrderPlaced notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed record StableNamed : INotification;

    private sealed record UnstableNamed : INotification;

    private sealed class IdempotentRequest : CommandBase, IIdempotentRequest
    {
        public string IdempotencyKey => "key";
    }

    private sealed class RetryableRequest : CommandBase, IRetryableRequest;

    // A custom serializer (not the generated one) that names StableNamed alone.
    private sealed class FakeNotificationSerializer() : SingleTypeNotificationSerializer<StableNamed>("fake.stable-named");

    private sealed class HandlerA : INotificationHandler<StableNamed>, INotificationHandler<UnstableNamed>
    {
        public Task Handle(StableNamed notification, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task Handle(UnstableNamed notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class HandlerB : INotificationHandler<StableNamed>, INotificationHandler<UnstableNamed>
    {
        public Task Handle(StableNamed notification, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task Handle(UnstableNamed notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public void CQRCONF009_errors_when_two_handler_types_share_a_name()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore, NullOutboxStore>();
        services.AddSingleton<INotificationSerializer, FakeNotificationSerializer>();
        services.AddSingleton<INotificationSubscriptionRegistry>(new FakeSubscriptionRegistry(
            NotificationSubscription.For<HandlerA, StableNamed>("shared.name"),
            NotificationSubscription.For<HandlerB, UnstableNamed>("shared.name")));
        using var provider = services.BuildServiceProvider();

        var issue = Inspect(provider, OutboxMode.Enabled).Should().ContainSingle(i => i.Code == "CQRCONF009").Subject;

        issue.Severity.Should().Be(CqrsBindingIssueSeverity.Error);
        issue.Message.Should().Contain("shared.name").And.Contain(typeof(HandlerA).FullName!).And.Contain(typeof(HandlerB).FullName!);
    }

    [Fact]
    public void CQRCONF009_not_reported_when_one_handler_type_subscribes_to_several_notifications_under_its_name()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore, NullOutboxStore>();
        services.AddSingleton<INotificationSerializer, FakeNotificationSerializer>();
        services.AddSingleton<INotificationSubscriptionRegistry>(new FakeSubscriptionRegistry(
            NotificationSubscription.For<HandlerA, StableNamed>("handler.a"),
            NotificationSubscription.For<HandlerA, UnstableNamed>("handler.a"),
            NotificationSubscription.For<HandlerB, StableNamed>("handler.b")));
        using var provider = services.BuildServiceProvider();

        Inspect(provider, OutboxMode.Enabled).Should().NotContain(i => i.Code == "CQRCONF009");
    }

    [Fact]
    public void CQRCONF009_not_reported_when_outbox_disabled()
    {
        var services = new ServiceCollection();
        services.AddSingleton<INotificationSubscriptionRegistry>(new FakeSubscriptionRegistry(
            NotificationSubscription.For<HandlerA, StableNamed>("shared.name"),
            NotificationSubscription.For<HandlerB, UnstableNamed>("shared.name")));
        using var provider = services.BuildServiceProvider();

        Inspect(provider, OutboxMode.Disabled).Should().NotContain(i => i.Code == "CQRCONF009");
    }

    private sealed class CompletedAudit : INotificationHandler<CommandCompletedNotification>
    {
        public Task Handle(CommandCompletedNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class QueryAudit : INotificationHandler<QueryCompletedNotification<int>>
    {
        public Task Handle(QueryCompletedNotification<int> notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
