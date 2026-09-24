using CQRSharp.Core.Notifications;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CQRSharp.Tests.Core;

/// <summary>
///     The observable semantics of every <see cref="PublishStrategy" /> when one notification fans out to several
///     handlers and one or more of them fail, whether a handler faults its task or throws before returning one:
///     <list type="bullet">
///         <item><see cref="PublishStrategy.Sequential" /> runs handlers one at a time and stops at the first failure.</item>
///         <item><see cref="PublishStrategy.Parallel" /> starts every handler and surfaces the first failure as itself.</item>
///         <item>
///             <see cref="PublishStrategy.ParallelWhenAllAggregate" /> starts every handler; several failures surface as
///             one <see cref="AggregateException" />, a single failure as itself.
///         </item>
///     </list>
///     The handlers are registered by hand, in order; that they ran is proven by per-handler flags, not only by the
///     exception.
/// </summary>
public sealed class PublishStrategyTests
{
    private static Harness Build(PublishStrategy strategy, params INotificationHandler<StrategyNotification>[] handlers)
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.Configure<NotificationOptions>(o => o.PublishStrategy = strategy);
        foreach (var handler in handlers)
            services.AddSingleton(handler);
        return new Harness(services.BuildServiceProvider());
    }

    [Fact(DisplayName = "Sequential stops at the first failing handler; later handlers do not run")]
    public async Task Sequential_StopsAtFirstFailure_LaterHandlersDoNotRun()
    {
        var first = new FaultingHandler("first");
        var later = new SuccessHandler();
        await using var harness = Build(PublishStrategy.Sequential, first, later);

        var act = () => harness.Dispatcher.Publish(new StrategyNotification(), CancellationToken.None);

        await act.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage("first");
        first.Ran.Should().BeTrue("the first handler runs and is the one that fails");
        later.Ran.Should().BeFalse("sequential dispatch must stop at the first failing handler");
    }

    [Theory(DisplayName = "Parallel runs every handler even when one fails, and surfaces that failure as itself")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Parallel_RunsAllHandlers_AndSurfacesFailure(bool throwsSynchronously)
    {
        var failing = throwsSynchronously ? (IFlaggedHandler)new ThrowingHandler("boom") : new FaultingHandler("boom");
        var succeeding = new SuccessHandler();
        var alsoSucceeding = new SuccessHandler();
        await using var harness = Build(PublishStrategy.Parallel, failing, succeeding, alsoSucceeding);

        var act = () => harness.Dispatcher.Publish(new StrategyNotification(), CancellationToken.None);

        await act.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage("boom");
        failing.Ran.Should().BeTrue("the failing handler is started");
        succeeding.Ran.Should().BeTrue("parallel dispatch starts every handler, not just up to the failure");
        alsoSucceeding.Ran.Should().BeTrue("parallel dispatch starts every handler, not just up to the failure");
    }

    [Fact(DisplayName = "Parallel surfaces only the first failure, not an aggregate, when several handlers fail")]
    public async Task Parallel_MultipleFailures_SurfacesSingleFailureNotAggregate()
    {
        var first = new FaultingHandler("A");
        var second = new FaultingHandler("B");
        await using var harness = Build(PublishStrategy.Parallel, first, second);

        var act = () => harness.Dispatcher.Publish(new StrategyNotification(), CancellationToken.None);

        // Both tasks are already faulted, so await rethrows the first in start order.
        await act.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage("A");
        first.Ran.Should().BeTrue();
        second.Ran.Should().BeTrue("both handlers are started even though only the first failure is surfaced");
    }

    [Fact(DisplayName = "ParallelWhenAllAggregate runs all handlers and aggregates every failure, a synchronous throw included")]
    public async Task ParallelWhenAllAggregate_RunsAll_AndAggregatesEveryFailure()
    {
        var failA = new ThrowingHandler("A");
        var succeeding = new SuccessHandler();
        var failB = new FaultingHandler("B");
        await using var harness = Build(PublishStrategy.ParallelWhenAllAggregate, failA, succeeding, failB);

        var act = () => harness.Dispatcher.Publish(new StrategyNotification(), CancellationToken.None);

        var assertion = await act.Should().ThrowExactlyAsync<AggregateException>();
        assertion.Which.InnerExceptions.Select(e => e.Message)
            .Should().BeEquivalentTo(new[] { "A", "B" }, "every handler failure must be aggregated, not just the first");
        failA.Ran.Should().BeTrue();
        failB.Ran.Should().BeTrue();
        succeeding.Ran.Should().BeTrue("the successful handler still runs alongside the failing ones");
    }

    [Theory(DisplayName = "ParallelWhenAllAggregate with a single failure surfaces it as itself, not wrapped")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ParallelWhenAllAggregate_SingleFailure_IsNotWrapped(bool throwsSynchronously)
    {
        var failing = throwsSynchronously ? (IFlaggedHandler)new ThrowingHandler("solo") : new FaultingHandler("solo");
        var succeeding = new SuccessHandler();
        await using var harness = Build(PublishStrategy.ParallelWhenAllAggregate, failing, succeeding);

        var act = () => harness.Dispatcher.Publish(new StrategyNotification(), CancellationToken.None);

        await act.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage("solo");
        failing.Ran.Should().BeTrue();
        succeeding.Ran.Should().BeTrue("all handlers run regardless of how many fail");
    }

    [Fact(DisplayName = "Sequential runs the generated handlers in handler-name order, then the handlers registered by hand")]
    public async Task Sequential_order_is_handler_name_then_registration()
    {
        var recorder = new FanOutRecorder();
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddSingleton(recorder);
        services.AddSingleton<INotificationHandler<FanOutOrderPlaced>>(new RecordingHandler(recorder, "by-hand-1"));
        services.AddSingleton<INotificationHandler<FanOutOrderPlaced>>(new RecordingHandler(recorder, "by-hand-2"));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IDirectNotificationDispatcher>().Publish(new FanOutOrderPlaced(), CancellationToken.None);

        recorder.Deliveries.Should().Equal(
            "tests.fanout.auditor", "tests.fanout.base", "tests.fanout.both:derived", "tests.fanout.own", "by-hand-1", "by-hand-2");
    }

    [Fact(DisplayName = "An undefined PublishStrategy fails host start")]
    public async Task Undefined_strategy_fails_host_start()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddCqrsGenerated();
                services.Configure<NotificationOptions>(o => o.PublishStrategy = (PublishStrategy)42);
            })
            .Build();

        var act = () => host.StartAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<OptionsValidationException>()).WithMessage("*PublishStrategy*");
    }

    [Fact(DisplayName = "Without a host, an undefined PublishStrategy fails the dispatcher's resolution, before any handler can run")]
    public async Task Undefined_strategy_fails_without_a_host()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.Configure<NotificationOptions>(o => o.PublishStrategy = (PublishStrategy)42);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var act = () => scope.ServiceProvider.GetRequiredService<IDirectNotificationDispatcher>();

        act.Should().Throw<OptionsValidationException>().WithMessage("*PublishStrategy*");
    }

    private sealed record StrategyNotification : INotification;

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly AsyncServiceScope _scope;

        public Harness(ServiceProvider provider)
        {
            _provider = provider;
            _scope = provider.CreateAsyncScope();
            Dispatcher = _scope.ServiceProvider.GetRequiredService<IDirectNotificationDispatcher>();
        }

        public IDirectNotificationDispatcher Dispatcher { get; }

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }

    private interface IFlaggedHandler : INotificationHandler<StrategyNotification>
    {
        bool Ran { get; }
    }

    /// <summary>Records that it ran, then faults its task with a distinguishable message.</summary>
    private sealed class FaultingHandler(string message) : IFlaggedHandler
    {
        public bool Ran { get; private set; }

        public Task Handle(StrategyNotification notification, CancellationToken cancellationToken)
        {
            Ran = true;
            return Task.FromException(new InvalidOperationException(message));
        }
    }

    /// <summary>Records that it ran, then throws before returning a task.</summary>
    private sealed class ThrowingHandler(string message) : IFlaggedHandler
    {
        public bool Ran { get; private set; }

        public Task Handle(StrategyNotification notification, CancellationToken cancellationToken)
        {
            Ran = true;
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>Records that it ran and completes successfully.</summary>
    private sealed class SuccessHandler : IFlaggedHandler
    {
        public bool Ran { get; private set; }

        public Task Handle(StrategyNotification notification, CancellationToken cancellationToken)
        {
            Ran = true;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHandler(FanOutRecorder recorder, string name) : INotificationHandler<FanOutOrderPlaced>
    {
        public Task Handle(FanOutOrderPlaced notification, CancellationToken cancellationToken)
        {
            recorder.Add(name);
            return Task.CompletedTask;
        }
    }
}
