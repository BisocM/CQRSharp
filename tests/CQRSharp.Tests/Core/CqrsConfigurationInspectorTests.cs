using System.Data;
using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Abstractions.Interfaces.Outbox;
using CQRSharp.Abstractions.Interfaces.Transactions;
using CQRSharp.Abstractions.Models.Outbox;
using CQRSharp.Pipelines.Behaviors.Idempotency;
using CQRSharp.Pipelines.Behaviors.Resilience;
using CQRSharp.Core.Caching.Requests;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Extensions;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Options;
using CQRSharp.Core.Options.Enums;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Unit tests for <see cref="CqrsConfigurationInspector.Inspect" />, driving it with a hand-built
///     <see cref="ServiceCollection" /> to exercise each CQRCONF code in isolation, plus a generator/behavior
///     test that the source-generated notification registry is emitted and resolvable.
/// </summary>
public sealed class CqrsConfigurationInspectorTests
{
    private static IReadOnlyList<CqrsBindingIssue> Inspect(
        IServiceProvider services,
        OutboxMode mode,
        IReadOnlyList<CqrsRequestBinding>? requestBindings = null,
        ICqrsNotificationRegistry? notifications = null)
        => CqrsConfigurationInspector.Inspect(
            services,
            new OutboxOptions { Mode = mode },
            new DispatcherOptions(),
            requestBindings ?? Array.Empty<CqrsRequestBinding>(),
            notifications);

    private static CqrsRequestBinding Binding()
        => new(
            typeof(object),
            typeof(object),
            null,
            null,
            Array.Empty<Type>(),
            Array.Empty<CqrsInterceptorBinding>(),
            Array.Empty<CqrsInterceptorBinding>(),
            Array.Empty<CqrsPipelineBehaviorBinding>(),
            Array.Empty<CqrsPipelineBehaviorBinding>(),
            Array.Empty<CqrsBindingIssue>());

    private static CqrsRequestBinding BindingFor(
        Type requestType,
        CqrsPipelineBehaviorBinding? pipeline = null,
        CqrsPipelineBehaviorBinding? exempted = null)
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
            Array.Empty<CqrsBindingIssue>());

    [Fact]
    public void CQRCONF001_reported_when_outbox_enabled_but_services_missing()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        var issues = Inspect(provider, OutboxMode.Enabled);

        var issue = issues.Should().ContainSingle(i => i.Code == "CQRCONF001").Subject;
        issue.Severity.Should().Be(CqrsBindingIssueSeverity.Error);
        issue.Message.Should().Contain(nameof(IOutboxStore));
        issue.Message.Should().Contain(nameof(INotificationSerializer));
        issue.Message.Should().Contain(nameof(IDirectNotificationDispatcher));
        issue.Message.Should().Contain("UseOutbox");
    }

    [Fact]
    public void CQRCONF001_lists_only_the_missing_services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore, FakeOutboxStore>();
        using var provider = services.BuildServiceProvider();

        var issue = Inspect(provider, OutboxMode.Enabled).Should().ContainSingle(i => i.Code == "CQRCONF001").Subject;

        issue.Message.Should().NotContain(nameof(IOutboxStore) + ",");
        issue.Message.Should().Contain(nameof(INotificationSerializer));
        issue.Message.Should().Contain(nameof(IDirectNotificationDispatcher));
    }

    [Fact]
    public void CQRCONF001_cleared_when_all_outbox_services_present()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore, FakeOutboxStore>();
        services.AddSingleton<INotificationSerializer, FakeNotificationSerializer>();
        services.AddSingleton<IDirectNotificationDispatcher, FakeDirectNotificationDispatcher>();
        using var provider = services.BuildServiceProvider();

        Inspect(provider, OutboxMode.Enabled).Should().NotContain(i => i.Code == "CQRCONF001");
    }

    [Fact]
    public void CQRCONF001_not_reported_when_outbox_disabled()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        Inspect(provider, OutboxMode.Disabled).Should().BeEmpty();
    }

    [Fact]
    public async Task CQRCONF002_warns_when_transactional_with_non_explicit_unit_of_work()
    {
        var services = new ServiceCollection();
        // Supply the outbox triplet so only CQRCONF002 is in play.
        services.AddSingleton<IOutboxStore, FakeOutboxStore>();
        services.AddSingleton<INotificationSerializer, FakeNotificationSerializer>();
        services.AddSingleton<IDirectNotificationDispatcher, FakeDirectNotificationDispatcher>();
        services.AddScoped<IUnitOfWork, NonExplicitUnitOfWork>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var issue = Inspect(scope.ServiceProvider, OutboxMode.Transactional)
            .Should().ContainSingle(i => i.Code == "CQRCONF002").Subject;

        issue.Severity.Should().Be(CqrsBindingIssueSeverity.Warning);
        issue.Message.Should().Contain(typeof(NonExplicitUnitOfWork).FullName!);
    }

    [Fact]
    public async Task CQRCONF002_not_reported_for_explicit_unit_of_work()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore, FakeOutboxStore>();
        services.AddSingleton<INotificationSerializer, FakeNotificationSerializer>();
        services.AddSingleton<IDirectNotificationDispatcher, FakeDirectNotificationDispatcher>();
        services.AddScoped<IUnitOfWork, ExplicitUnitOfWork>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        Inspect(scope.ServiceProvider, OutboxMode.Transactional).Should().NotContain(i => i.Code == "CQRCONF002");
    }

    [Fact]
    public async Task CQRCONF002_not_reported_when_mode_enabled_not_transactional()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore, FakeOutboxStore>();
        services.AddSingleton<INotificationSerializer, FakeNotificationSerializer>();
        services.AddSingleton<IDirectNotificationDispatcher, FakeDirectNotificationDispatcher>();
        services.AddScoped<IUnitOfWork, NonExplicitUnitOfWork>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        Inspect(scope.ServiceProvider, OutboxMode.Enabled).Should().NotContain(i => i.Code == "CQRCONF002");
    }

    [Fact]
    public void CQRCONF003_warns_per_handled_notification_without_stable_name()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore, FakeOutboxStore>();
        services.AddSingleton<INotificationSerializer, FakeNotificationSerializer>();
        services.AddSingleton<IDirectNotificationDispatcher, FakeDirectNotificationDispatcher>();
        using var provider = services.BuildServiceProvider();

        var notifications = new FakeNotificationRegistry(
            new[] { typeof(StableNamed), typeof(UnstableNamed) },
            stableNamed: typeof(StableNamed));

        var issues = Inspect(provider, OutboxMode.Enabled, notifications: notifications);

        var conf003 = issues.Where(i => i.Code == "CQRCONF003").ToArray();
        conf003.Should().ContainSingle();
        conf003[0].Severity.Should().Be(CqrsBindingIssueSeverity.Warning);
        conf003[0].Message.Should().Contain(typeof(UnstableNamed).FullName!);
    }

    [Fact]
    public void CQRCONF003_not_reported_when_outbox_disabled()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        var notifications = new FakeNotificationRegistry(new[] { typeof(UnstableNamed) }, stableNamed: null);

        Inspect(provider, OutboxMode.Disabled, notifications: notifications).Should().BeEmpty();
    }

    [Fact]
    public void CQRCONF004_reported_when_request_registry_missing_and_bindings_exist()
    {
        // Disabled mode and no notifications so only CQRCONF004 can fire.
        using var provider = new ServiceCollection().BuildServiceProvider();

        var issue = Inspect(provider, OutboxMode.Disabled, requestBindings: new[] { Binding() })
            .Should().ContainSingle(i => i.Code == "CQRCONF004").Subject;

        issue.Severity.Should().Be(CqrsBindingIssueSeverity.Error);
        issue.Message.Should().Contain("AddCqrsGenerated");
    }

    [Fact]
    public void CQRCONF004_not_reported_when_request_registry_present()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetService<IRequestRegistry>().Should().NotBeNull();

        Inspect(scope.ServiceProvider, OutboxMode.Disabled, requestBindings: new[] { Binding() })
            .Should().NotContain(i => i.Code == "CQRCONF004");
    }

    [Fact]
    public void CQRCONF005_warns_when_idempotent_request_has_no_idempotency_behavior()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        var issue = Inspect(provider, OutboxMode.Disabled, requestBindings: new[] { BindingFor(typeof(IdempotentRequest)) })
            .Should().ContainSingle(i => i.Code == "CQRCONF005").Subject;

        issue.Severity.Should().Be(CqrsBindingIssueSeverity.Warning);
        issue.Message.Should().Contain(typeof(IdempotentRequest).FullName!);
        issue.Message.Should().Contain("UseIdempotency");
    }

    [Fact]
    public void CQRCONF005_cleared_when_idempotency_behavior_is_wired()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        var binding = BindingFor(
            typeof(IdempotentRequest),
            new CqrsPipelineBehaviorBinding(typeof(IdempotencyBehavior<IdempotentRequest, object>), 0, false));

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
            exempted: new CqrsPipelineBehaviorBinding(typeof(IdempotencyBehavior<IdempotentRequest, object>), 0, true));

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
            new CqrsPipelineBehaviorBinding(typeof(ResilienceBehavior<RetryableRequest, object>), 0, false));

        Inspect(provider, OutboxMode.Disabled, requestBindings: new[] { binding })
            .Should().NotContain(i => i.Code == "CQRCONF006");
    }

    [Fact]
    public void CQRCONF005_and_006_not_reported_for_a_request_without_the_markers()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        var issues = Inspect(provider, OutboxMode.Disabled, requestBindings: new[] { Binding() });

        issues.Should().NotContain(i => i.Code == "CQRCONF005" || i.Code == "CQRCONF006");
    }

    [Fact]
    public void Generated_notification_registry_is_resolvable_and_reports_stable_names()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<ICqrsNotificationRegistry>();

        registry.HandledNotificationTypes.Should().Contain(typeof(CQRSharp.Tests.Shared.TestNotification));
        registry.HasStableName(typeof(CQRSharp.Tests.Shared.TestNotification)).Should().BeTrue();
        registry.HasStableName(typeof(object)).Should().BeFalse();
    }

    [Fact]
    public void DescribeConfiguration_is_clean_for_a_fully_wired_disabled_outbox()
    {
        // The test assembly has discovered idempotent/retryable requests, so a "fully wired" clean setup must also
        // enable those behaviors (otherwise CQRCONF005/006 would fire); the outbox stays off (Disabled).
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b
            .UseIdempotency(i => i.UseInMemoryStore())
            .UseResilience(o => { }));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var diagnostics = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>();

        diagnostics.DescribeConfiguration().Should().BeEmpty();
    }

    [Fact]
    public void DescribeConfiguration_reports_CQRCONF001_when_outbox_enabled_without_store()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(configureOutbox: o => o.Mode = OutboxMode.Enabled);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var diagnostics = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>();

        diagnostics.DescribeConfiguration().Should().Contain(i => i.Code == "CQRCONF001");
    }

    private sealed class FakeNotificationRegistry(IReadOnlyList<Type> handled, Type? stableNamed)
        : ICqrsNotificationRegistry
    {
        public IReadOnlyList<Type> HandledNotificationTypes { get; } = handled;
        public bool HasStableName(Type notificationType) => stableNamed is not null && notificationType == stableNamed;
    }

    private sealed class StableNamed;

    private sealed class UnstableNamed;

    private sealed class IdempotentRequest : CommandBase, IIdempotentRequest
    {
        public string IdempotencyKey => "key";
    }

    private sealed class RetryableRequest : CommandBase, IRetryableRequest;

    private sealed class NonExplicitUnitOfWork : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(0);
        public TService GetService<TService>() where TService : class => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ExplicitUnitOfWork : IExplicitUnitOfWork
    {
        public bool HasActiveTransaction => false;
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(0);
        public TService GetService<TService>() where TService : class => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CreateSavepointAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReleaseSavepointAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeOutboxStore : IOutboxStore
    {
        public Task StoreAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IEnumerable<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken)
            => Task.FromResult<IEnumerable<OutboxMessage>>(Array.Empty<OutboxMessage>());
        public Task MarkAsProcessedAsync(Guid messageId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task MarkAsFailedAsync(Guid messageId, string? error, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<int> IncrementAttemptAsync(Guid messageId, string? error, DateTime? nextRetryAt, CancellationToken cancellationToken)
            => Task.FromResult(1);
    }

    private sealed class FakeNotificationSerializer : INotificationSerializer
    {
        public byte[] Serialize(INotification notification) => Array.Empty<byte>();
        public INotification? Deserialize(string notificationName, byte[] payload) => null;
        public string GetNotificationName(Type notificationType) => notificationType.FullName ?? notificationType.Name;
    }

    private sealed class FakeDirectNotificationDispatcher : IDirectNotificationDispatcher
    {
        public Task Publish(INotification notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
