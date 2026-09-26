using CQRSharp.Core.Modules;
using CQRSharp.Core.Notifications;
using CQRSharp.Pipelines;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CQRSharp.Tests.Core;

/// <summary>
///     What an in-process publish asks the container once per provider and notification type, and never again: whether
///     handlers can have been registered by hand, whether any behavior is registered, and whom the type's subscriptions
///     reach. A plan belongs to one provider, so several providers in one process (test hosts, several hosts) each
///     publish by their own registrations, and the one publisher of a provider takes the scope to resolve from on every
///     call.
/// </summary>
public sealed class NotificationPlanTests
{
    [Fact(DisplayName = "Two providers with different registrations in one process each publish by their own plan")]
    public async Task Each_provider_publishes_by_its_own_plan()
    {
        var withHandler = new PlanTrace();
        var withBehavior = new PlanTrace();
        await using var handlerProvider = Build(withHandler, s => s.AddTransient<INotificationHandler<PlanProbe>, HandRegisteredPlanHandler>());
        await using var behaviorProvider = Build(withBehavior, s => s.AddTransient<INotificationPipelineBehavior<PlanProbe>, PlanBehavior>());

        // Interleaved, so each publish finds the other provider's plan in the static slot first.
        for (var i = 0; i < 3; i++)
        {
            await PublishInNewScope(handlerProvider, new PlanProbe());
            await PublishInNewScope(behaviorProvider, new PlanProbe());
        }

        withHandler.Entries.Should().Equal("handler", "handler", "handler");
        withBehavior.Entries.Should().Equal("behavior", "behavior", "behavior");
    }

    [Fact(DisplayName = "A provider built after another was disposed publishes by its own registrations")]
    public async Task A_disposed_providers_plan_is_not_reused()
    {
        var first = new PlanTrace();
        await using (var provider = Build(first, s => s.AddTransient<INotificationPipelineBehavior<PlanProbe>, PlanBehavior>()))
            await PublishInNewScope(provider, new PlanProbe());

        var second = new PlanTrace();
        await using (var provider = Build(second, s => s.AddTransient<INotificationHandler<PlanProbe>, HandRegisteredPlanHandler>()))
            await PublishInNewScope(provider, new PlanProbe());

        first.Entries.Should().Equal("behavior");
        second.Entries.Should().Equal("handler");
    }

    [Fact(DisplayName = "The provider-wide publisher resolves the handlers from the scope it is publishing from")]
    public async Task Handlers_come_from_the_publishing_scope()
    {
        var trace = new PlanTrace();
        await using var provider = Build(trace, s =>
        {
            s.AddScoped<ScopeMarker>();
            s.AddTransient<INotificationHandler<PlanProbe>, ScopeRecordingHandler>();
        });

        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        var firstDispatcher = first.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        await firstDispatcher.Publish(new PlanProbe(), TestContext.Current.CancellationToken);
        await second.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish(new PlanProbe(), TestContext.Current.CancellationToken);
        await firstDispatcher.Publish(new PlanProbe(), TestContext.Current.CancellationToken);

        var firstMarker = first.ServiceProvider.GetRequiredService<ScopeMarker>().Id;
        var secondMarker = second.ServiceProvider.GetRequiredService<ScopeMarker>().Id;
        firstMarker.Should().NotBe(secondMarker);
        trace.Entries.Should().Equal(firstMarker, secondMarker, firstMarker);
    }

    [Fact(DisplayName = "A lifecycle subscriber registered in one provider publishes that provider's lifecycle notifications, not the other's")]
    public async Task Lifecycle_subscribers_are_per_provider()
    {
        var subscribed = new PlanTrace();
        var unsubscribed = new PlanTrace();
        await using var subscribedProvider = Build(subscribed, s => s.AddTransient<INotificationHandler<CommandCompletedNotification>, CommandCompletedRecorder>());
        await using var unsubscribedProvider = Build(unsubscribed, _ => { });

        for (var i = 0; i < 2; i++)
        {
            await SendInNewScope(unsubscribedProvider);
            await SendInNewScope(subscribedProvider);
        }

        subscribed.Entries.Should().Equal("completed", "completed");
        unsubscribed.Entries.Should().BeEmpty();
    }

    [Fact(DisplayName = "A container that cannot answer registration queries runs every step: hand-registered handlers and behaviors are resolved")]
    public async Task Without_registration_queries_every_step_runs()
    {
        var trace = new PlanTrace();
        await using var provider = Build(trace, s =>
        {
            s.AddTransient<INotificationHandler<PlanProbe>, HandRegisteredPlanHandler>();
            s.AddTransient<INotificationPipelineBehavior<PlanProbe>, PlanBehavior>();
        });
        using var publisher = new NotificationPublisher(
            new WithoutRegistrationQueries(provider),
            provider.GetServices<ICqrsModule>(),
            provider.GetRequiredService<INotificationSubscriptionRegistry>(),
            new NotificationOptions(),
            new OutboxOptions(),
            metrics: null,
            NullLogger.Instance);
        await using var scope = provider.CreateAsyncScope();

        await publisher.PublishInProcess(scope.ServiceProvider, new PlanProbe(), TestContext.Current.CancellationToken);
        await publisher.PublishInProcess<INotification>(scope.ServiceProvider, new PlanProbe(), TestContext.Current.CancellationToken);

        trace.Entries.Should().Equal("behavior", "handler", "behavior", "handler");
    }

    private static ServiceProvider Build(PlanTrace trace, Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddSingleton(trace);
        configure(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static async Task PublishInNewScope(IServiceProvider provider, PlanProbe notification)
    {
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish(notification, TestContext.Current.CancellationToken);
    }

    private static async Task SendInNewScope(IServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new PlanCommand(), TestContext.Current.CancellationToken))
            .IsSuccess.Should().BeTrue();
    }

    // Private, so the generator neither subscribes these handlers nor registers this behavior as a discovered one: what
    // reaches PlanProbe in a provider is only what that provider's test registers.
    private sealed class HandRegisteredPlanHandler(PlanTrace trace) : INotificationHandler<PlanProbe>
    {
        public Task Handle(PlanProbe notification, CancellationToken cancellationToken)
        {
            trace.Add("handler");
            return Task.CompletedTask;
        }
    }

    private sealed class ScopeRecordingHandler(PlanTrace trace, ScopeMarker marker) : INotificationHandler<PlanProbe>
    {
        public Task Handle(PlanProbe notification, CancellationToken cancellationToken)
        {
            trace.Add(marker.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class PlanBehavior(PlanTrace trace) : INotificationPipelineBehavior<PlanProbe>
    {
        public Task Handle(PlanProbe notification, NotificationHandlerDelegate next, CancellationToken cancellationToken)
        {
            trace.Add("behavior");
            return next(cancellationToken);
        }
    }

    private sealed class CommandCompletedRecorder(PlanTrace trace) : INotificationHandler<CommandCompletedNotification>
    {
        public Task Handle(CommandCompletedNotification notification, CancellationToken cancellationToken)
        {
            if (notification.Command is PlanCommand) trace.Add("completed");
            return Task.CompletedTask;
        }
    }

    private sealed class WithoutRegistrationQueries(IServiceProvider inner) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(IServiceProviderIsService) || serviceType == typeof(IServiceProviderIsKeyedService)
                ? null
                : inner.GetService(serviceType);
    }
}

/// <summary>A notification no generated handler subscribes to: whatever reaches it is registered by each test.</summary>
public sealed record PlanProbe : INotification;

public sealed class PlanCommand : CommandBase;

public sealed class PlanCommandHandler : ICommandHandler<PlanCommand>
{
    public Task<CommandResult> Handle(PlanCommand command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}

public sealed class PlanTrace
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

public sealed class ScopeMarker
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
}
