using CQRSharp.Core.Notifications;
using CQRSharp.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CQRSharp.Tests.Core;

/// <summary>
///     The one fan-out rule: a notification of runtime type R reaches every generated handler declared for R, a base
///     type or an interface of R, each once, at the nearest of its declared types, plus the handlers registered by hand
///     for R. The same set whatever type it is published as, whichever dispatcher publishes it, and whether it is
///     delivered in-process or through the outbox.
/// </summary>
public sealed class NotificationFanOutTests
{
    private static readonly string[] EveryHandler =
        ["tests.fanout.auditor", "tests.fanout.base", "tests.fanout.both:derived", "tests.fanout.own"];

    [Fact(DisplayName = "A notification reaches the handlers of its type, of its base type and of its interface, each once")]
    public async Task Reaches_every_assignable_handler()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish(new FanOutOrderPlaced(), TestContext.Current.CancellationToken);

        provider.GetRequiredService<FanOutRecorder>().Deliveries.Should().BeEquivalentTo(EveryHandler);
    }

    [Fact(DisplayName = "Typed, widened and untyped publishes reach the same handlers")]
    public async Task Every_publish_path_reaches_the_same_handlers()
    {
        foreach (var publish in new Func<IServiceProvider, Task>[]
                 {
                     sp => sp.GetRequiredService<ICqrsDispatcher>().Publish(new FanOutOrderPlaced(), TestContext.Current.CancellationToken),
                     sp => sp.GetRequiredService<ICqrsDispatcher>().Publish<INotification>(new FanOutOrderPlaced(), TestContext.Current.CancellationToken),
                     sp => sp.GetRequiredService<ICqrsDispatcher>().Publish<FanOutBaseEvent>(new FanOutOrderPlaced(), TestContext.Current.CancellationToken)
                 })
        {
            await using var provider = Build();
            await using var scope = provider.CreateAsyncScope();

            await publish(scope.ServiceProvider);

            provider.GetRequiredService<FanOutRecorder>().Deliveries.Should().BeEquivalentTo(EveryHandler);
        }
    }

    [Fact(DisplayName = "A handler declared for a notification and its base type runs once, for the nearer type")]
    public async Task Handler_declared_at_two_levels_runs_once()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        await dispatcher.Publish(new FanOutOrderPlaced(), TestContext.Current.CancellationToken);
        await dispatcher.Publish(new FanOutBaseEvent(), TestContext.Current.CancellationToken);

        var both = provider.GetRequiredService<FanOutRecorder>().Deliveries.Where(d => d.StartsWith("tests.fanout.both", StringComparison.Ordinal));
        both.Should().Equal("tests.fanout.both:derived", "tests.fanout.both:base");
    }

    [Fact(DisplayName = "A handler registered by hand for the notification's type runs too, after the generated ones")]
    public async Task Hand_registered_handler_runs_after_the_generated_ones()
    {
        var recorder = new FanOutRecorder();
        var handRegistered = new Mock<INotificationHandler<FanOutOrderPlaced>>();
        handRegistered.Setup(h => h.Handle(It.IsAny<FanOutOrderPlaced>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                recorder.Add("by-hand");
                return Task.CompletedTask;
            });
        await using var provider = Build(services =>
        {
            services.AddSingleton(recorder);
            services.AddSingleton(handRegistered.Object);
        });
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish(new FanOutOrderPlaced(), TestContext.Current.CancellationToken);

        recorder.Deliveries.Should().BeEquivalentTo([..EveryHandler, "by-hand"]);
        recorder.Deliveries.Last().Should().Be("by-hand");
    }

    [Fact(DisplayName = "A generated handler also registered by hand (an assembly scan) runs once")]
    public async Task Generated_handler_registered_by_hand_runs_once()
    {
        await using var provider = Build(services => services.AddTransient<INotificationHandler<FanOutOrderPlaced>, FanOutOwnHandler>());
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish(new FanOutOrderPlaced(), TestContext.Current.CancellationToken);

        provider.GetRequiredService<FanOutRecorder>().Deliveries.Should().BeEquivalentTo(EveryHandler);
    }

    [Fact(DisplayName = "A notification reaches the same handlers in-process and through a transactional outbox, with or without a transaction")]
    public async Task In_process_and_outbox_reach_the_same_handlers()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        await using var provider = Build(services =>
        {
            services.AddScoped<IUnitOfWork>(_ => unitOfWork.Object);
            services.AddInMemoryOutboxStore();
            services.Configure<OutboxOptions>(o => o.Mode = OutboxMode.Transactional);
        });

        // No transaction: the notification is delivered in-process.
        unitOfWork.Setup(u => u.HasActiveTransaction).Returns(false);
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish(new FanOutOrderPlaced(), TestContext.Current.CancellationToken);

        // A transaction: it is stored in the outbox, one message per handler it will be delivered to.
        unitOfWork.Setup(u => u.HasActiveTransaction).Returns(true);
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish(new FanOutOrderPlaced(), TestContext.Current.CancellationToken);

        var stored = await provider.GetRequiredService<IOutboxStore>().ClaimPendingAsync(100, TestContext.Current.CancellationToken);
        var inProcess = provider.GetRequiredService<FanOutRecorder>().Deliveries.Select(d => d.Split(':')[0]);

        stored.Select(m => m.Message.HandlerName).Should().BeEquivalentTo(inProcess).And.HaveCount(4);
    }

    [Fact(DisplayName = "A handler declared for two tied interfaces runs once, for the declared type the outbox subscription names")]
    public async Task Tied_interfaces_resolve_alike_in_process_and_for_the_outbox()
    {
        await using var provider = Build(services => services.AddSingleton<TieRecorder>());
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish(new TieNotification(), TestContext.Current.CancellationToken);

        var subscribed = provider.GetRequiredService<INotificationSubscriptionRegistry>().GetSubscriptions(typeof(TieNotification))
            .Should().ContainSingle().Subject.NotificationType;
        provider.GetRequiredService<TieRecorder>().Seen.Should().Equal(subscribed == typeof(ITieEventA) ? "plain" : "generic");
    }

    private static ServiceProvider Build(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddSingleton<FanOutRecorder>();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
