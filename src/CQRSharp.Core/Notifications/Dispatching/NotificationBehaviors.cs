using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines;

namespace CQRSharp.Core.Notifications;

/// <summary>
///     The notification pipeline: the behaviors of one notification type, resolved and ordered, run around a terminal
///     step. An in-process publish and an outbox delivery both go through it, so a behavior wraps both the same way.
/// </summary>
internal static class NotificationBehaviors
{
    /// <summary>
    ///     The behaviors for <typeparamref name="TNotification" /> in priority order, on an array the caller owns: from
    ///     the container, or - for a value-type notification on a runtime without dynamic code, where the container cannot
    ///     close an open-generic behavior over it - from the closed set the modules' generated factories build; merged
    ///     with the closed ones the generator discovered, as a request's are.
    /// </summary>
    /// <param name="services">The scope to resolve them from.</param>
    /// <param name="routing">The provider's routing, which knows whether any was discovered; asked of the container without it.</param>
    public static INotificationPipelineBehavior<TNotification>[] Resolve<TNotification>(IServiceProvider services, NotificationRouting? routing)
        where TNotification : INotification
    {
        var mergeDiscovered = routing?.MayHaveDiscoveredBehaviors(typeof(INotificationPipelineBehavior<TNotification>))
                              ?? PipelineBehaviors.MayHaveDiscovered<INotificationPipelineBehavior<TNotification>>(services);
        var behaviors = PipelineBehaviors.ResolveAll<INotificationPipelineBehavior<TNotification>>(
            services, PipelineBehaviors.UsesClosedBehaviors<TNotification>(services), mergeDiscovered);
        if (behaviors.Length > 1) Array.Sort(behaviors, PriorityComparer<TNotification>.Instance);
        return behaviors;
    }

    /// <summary>Runs <paramref name="behaviors" /> in order around <paramref name="terminal" />.</summary>
    public static Task Run<TNotification>(
        INotificationPipelineBehavior<TNotification>[] behaviors,
        TNotification notification,
        NotificationHandlerDelegate terminal,
        CancellationToken cancellationToken)
        where TNotification : INotification
    {
        return Invoke(0, cancellationToken);

        Task Invoke(int index, CancellationToken ct)
            => index >= behaviors.Length
                ? terminal(ct)
                : behaviors[index].Handle(notification, next => Invoke(index + 1, next), ct);
    }

    // A handler that throws before its first await is reported through its task, as one that faults later is, so a
    // parallel fan-out never abandons handlers it already started.
    public static Task Start(NotificationSubscription subscription, IServiceProvider services, INotification notification, CancellationToken cancellationToken)
    {
        try
        {
            return subscription.Handle(services, notification, cancellationToken);
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    public static Task Start<TNotification>(INotificationHandler<TNotification> handler, TNotification notification, CancellationToken cancellationToken)
        where TNotification : INotification
    {
        try
        {
            return handler.Handle(notification, cancellationToken) ?? Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    private sealed class PriorityComparer<TNotification> : IComparer<INotificationPipelineBehavior<TNotification>>
        where TNotification : INotification
    {
        public static readonly PriorityComparer<TNotification> Instance = new();

        public int Compare(INotificationPipelineBehavior<TNotification>? x, INotificationPipelineBehavior<TNotification>? y)
            => ReferenceEquals(x, y) ? 0 : x is null ? -1 : y is null ? 1
                : PriorityOrdering.Compare(PriorityOf(x), PriorityOf(y), x, y);

        private static int PriorityOf(object behavior)
            => behavior is IPrioritizedPipelineBehavior prioritized
                ? prioritized.PipelineExecutionPriority
                : IPrioritizedPipelineBehavior.DefaultPriority;
    }
}
