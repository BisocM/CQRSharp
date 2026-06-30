using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Options;
using CQRSharp.Core.Options.Enums;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Matrix tests proving the observable semantics of every <see cref="PublishStrategy" /> when a single
///     notification fans out to multiple handlers and one or more of them fail:
///     <list type="bullet">
///         <item>
///             <see cref="PublishStrategy.Sequential" /> invokes handlers one at a time and stops at the first
///             failure — later handlers must NOT run.
///         </item>
///         <item>
///             <see cref="PublishStrategy.Parallel" /> starts every handler and surfaces a failure, but all
///             handlers run.
///         </item>
///         <item>
///             <see cref="PublishStrategy.ParallelWhenAllAggregate" /> starts every handler and aggregates EVERY
///             failure into an <see cref="AggregateException" />.
///         </item>
///     </list>
///     Handler execution is proven with per-handler flags/counters, not merely the thrown exception type.
/// </summary>
public class PublishStrategyTests
{
    private static (ServiceProvider provider, DirectNotificationDispatcher dispatcher) Build(
        PublishStrategy strategy,
        params INotificationHandler<StrategyNotification>[] handlers)
    {
        var services = new ServiceCollection();
        services.Configure<NotificationOptions>(o => o.PublishStrategy = strategy);
        foreach (var handler in handlers)
            services.AddSingleton(handler);
        var provider = services.BuildServiceProvider();
        return (provider, new DirectNotificationDispatcher(provider));
    }

    [Fact(DisplayName = "Sequential stops at the first failing handler; later handlers do not run")]
    public async Task Sequential_StopsAtFirstFailure_LaterHandlersDoNotRun()
    {
        var first = new ThrowingHandler("first");
        var later = new SuccessHandler();
        var (provider, dispatcher) = Build(PublishStrategy.Sequential, first, later);
        using var _ = provider;

        var act = () => dispatcher.Publish(new StrategyNotification(), CancellationToken.None);

        // Sequential surfaces the first failure directly (no aggregation).
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("first");
        first.Ran.Should().BeTrue("the first handler runs and is the one that fails");
        later.Ran.Should().BeFalse("sequential dispatch must stop at the first failing handler");
    }

    [Fact(DisplayName = "Parallel runs every handler even when one fails, and surfaces a failure")]
    public async Task Parallel_RunsAllHandlers_AndSurfacesFailure()
    {
        var failing = new ThrowingHandler("boom");
        var succeeding = new SuccessHandler();
        var alsoSucceeding = new SuccessHandler();
        var (provider, dispatcher) = Build(PublishStrategy.Parallel, failing, succeeding, alsoSucceeding);
        using var _ = provider;

        var act = () => dispatcher.Publish(new StrategyNotification(), CancellationToken.None);

        // Parallel propagates the failure (await rethrows the first faulted task).
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        failing.Ran.Should().BeTrue("the failing handler is started");
        succeeding.Ran.Should().BeTrue("parallel dispatch starts every handler, not just up to the failure");
        alsoSucceeding.Ran.Should().BeTrue("parallel dispatch starts every handler, not just up to the failure");
    }

    [Fact(DisplayName = "Parallel surfaces only the first failure, not an aggregate, when multiple handlers fail")]
    public async Task Parallel_MultipleFailures_SurfacesSingleFailureNotAggregate()
    {
        var first = new ThrowingHandler("A");
        var second = new ThrowingHandler("B");
        var (provider, dispatcher) = Build(PublishStrategy.Parallel, first, second);
        using var _ = provider;

        var act = () => dispatcher.Publish(new StrategyNotification(), CancellationToken.None);

        // Unlike ParallelWhenAllAggregate, the plain Parallel strategy lets await rethrow only the first failure.
        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.Should().NotBeOfType<AggregateException>();
        first.Ran.Should().BeTrue();
        second.Ran.Should().BeTrue("both handlers are started even though only the first failure is surfaced");
    }

    [Fact(DisplayName = "ParallelWhenAllAggregate runs all handlers and aggregates EVERY failure")]
    public async Task ParallelWhenAllAggregate_RunsAll_AndAggregatesEveryFailure()
    {
        var failA = new ThrowingHandler("A");
        var succeeding = new SuccessHandler();
        var failB = new ThrowingHandler("B");
        var (provider, dispatcher) = Build(
            PublishStrategy.ParallelWhenAllAggregate, failA, succeeding, failB);
        using var _ = provider;

        var act = () => dispatcher.Publish(new StrategyNotification(), CancellationToken.None);

        var assertion = await act.Should().ThrowAsync<AggregateException>();
        assertion.Which.InnerExceptions.Select(e => e.Message)
            .Should().BeEquivalentTo(new[] { "A", "B" }, "every handler failure must be aggregated, not just the first");
        failA.Ran.Should().BeTrue();
        failB.Ran.Should().BeTrue();
        succeeding.Ran.Should().BeTrue("the successful handler still runs alongside the failing ones");
    }

    [Fact(DisplayName = "ParallelWhenAllAggregate with a single failure surfaces it directly, not wrapped")]
    public async Task ParallelWhenAllAggregate_SingleFailure_IsNotWrapped()
    {
        var failing = new ThrowingHandler("solo");
        var succeeding = new SuccessHandler();
        var (provider, dispatcher) = Build(
            PublishStrategy.ParallelWhenAllAggregate, failing, succeeding);
        using var _ = provider;

        var act = () => dispatcher.Publish(new StrategyNotification(), CancellationToken.None);

        // With exactly one failure the dispatcher rethrows the original exception rather than wrapping it.
        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.Should().NotBeOfType<AggregateException>();
        assertion.Which.Message.Should().Be("solo");
        failing.Ran.Should().BeTrue();
        succeeding.Ran.Should().BeTrue("all handlers run regardless of how many fail");
    }

    private sealed record StrategyNotification : INotification;

    /// <summary>Records that it ran, then throws asynchronously with a distinguishable message.</summary>
    private sealed class ThrowingHandler : INotificationHandler<StrategyNotification>
    {
        private readonly string _message;

        public ThrowingHandler(string message) => _message = message;
        public bool Ran { get; private set; }

        public Task Handle(StrategyNotification notification, CancellationToken cancellationToken)
        {
            Ran = true;
            // Async (faulted-task) throw so that, under the parallel strategies, starting this handler does not
            // synchronously short-circuit the loop that starts its siblings.
            return Task.FromException(new InvalidOperationException(_message));
        }
    }

    /// <summary>Records that it ran and completes successfully.</summary>
    private sealed class SuccessHandler : INotificationHandler<StrategyNotification>
    {
        public bool Ran { get; private set; }

        public Task Handle(StrategyNotification notification, CancellationToken cancellationToken)
        {
            Ran = true;
            return Task.CompletedTask;
        }
    }
}