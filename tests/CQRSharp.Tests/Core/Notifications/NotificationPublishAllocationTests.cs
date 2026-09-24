using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     A publish to one generated handler, with nothing registered around it, allocates nothing of its own: the handler
///     the scope constructs is the only allocation. Counted on the calling thread, where the whole publish runs, since
///     the handler completes synchronously.
/// </summary>
public sealed class NotificationPublishAllocationTests
{
    private const int Publishes = 1000;

    [Fact(DisplayName = "A bare publish allocates only the handler instance, published as itself or as INotification")]
    public async Task Bare_publish_allocates_only_the_handler()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var notification = new AllocationProbe();

        // Warmed first: the plan, the container's call sites and the runtime's type loading allocate once.
        for (var i = 0; i < 100; i++)
        {
            await dispatcher.Publish(notification, TestContext.Current.CancellationToken);
            await dispatcher.Publish<INotification>(notification, TestContext.Current.CancellationToken);
            scope.ServiceProvider.GetRequiredService<AllocationProbeHandler>();
        }

        var handlerOnly = Measure(() => scope.ServiceProvider.GetRequiredService<AllocationProbeHandler>());
        var published = Measure(() => Completed(dispatcher.Publish(notification, CancellationToken.None)));
        var publishedAsInterface = Measure(() => Completed(dispatcher.Publish<INotification>(notification, CancellationToken.None)));

        handlerOnly.Should().BePositive();
        published.Should().Be(handlerOnly);
        publishedAsInterface.Should().Be(handlerOnly);
    }

    // Checked without an assertion library, which would allocate inside the measured loop.
    private static void Completed(Task publish)
    {
        if (!publish.IsCompletedSuccessfully) throw new InvalidOperationException("The publish did not complete synchronously.");
    }

    private static long Measure(Action publish)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Publishes; i++) publish();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}

public sealed record AllocationProbe : INotification;

public sealed class AllocationProbeHandler : INotificationHandler<AllocationProbe>
{
    public Task Handle(AllocationProbe notification, CancellationToken cancellationToken) => Task.CompletedTask;
}
