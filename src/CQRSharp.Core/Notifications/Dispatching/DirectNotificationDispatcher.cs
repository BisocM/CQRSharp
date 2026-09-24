using System.Runtime.ExceptionServices;
using CQRSharp.Pipelines;

namespace CQRSharp.Core.Notifications;

/// <summary>
///     The in-process publish: one rule for which handlers a notification reaches, whichever way it was published. A
///     notification of runtime type R is delivered to every subscription the <see cref="INotificationSubscriptionRegistry" />
///     gives R (every generated handler declared for R, a base type or an interface of R, each once), plus the handlers
///     registered by hand for the type it is delivered as. The notification pipeline behaviors of that type wrap the whole
///     fan-out once. The type it is delivered as is R when a module has a route for R, so publishing a domain event as
///     <see cref="INotification" /> behaves exactly like publishing it as itself; otherwise it is the type it was published as.
/// </summary>
internal sealed class DirectNotificationDispatcher(IServiceProvider services, NotificationRouting routing) : IDirectNotificationDispatcher
{
    public Task Publish(INotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        return routing.TryGetRoute(notification.GetType()) is { } route
            ? route.Publish(this, notification, cancellationToken)
            : PublishAs(notification, cancellationToken);
    }

    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);

        var type = notification.GetType();
        return type != typeof(TNotification) && routing.TryGetRoute(type) is { } route
            ? route.Publish(this, notification, cancellationToken)
            : PublishAs(notification, cancellationToken);
    }

    /// <summary>Publishes <paramref name="notification" /> as <typeparamref name="TNotification" />: its behaviors, then its handlers.</summary>
    internal Task PublishAs<TNotification>(TNotification notification, CancellationToken cancellationToken)
        where TNotification : INotification
    {
        if (routing.Metrics.NotificationsPublished.Enabled)
            routing.Metrics.RecordNotificationPublished(notification.GetType());

        var behaviors = NotificationBehaviors.Resolve<TNotification>(services, routing);

        // The overwhelmingly common case - no notification behaviors - goes straight to the handlers.
        return behaviors.Length == 0
            ? Deliver(notification, cancellationToken)
            : DeliverThroughBehaviors(behaviors, notification, cancellationToken);
    }

    // The terminal step's closure captures the notification. Built in a method of its own, it is only allocated when a
    // chain runs: a lambda over a parameter of PublishAs would be allocated on every publish, behaviors or not.
    private Task DeliverThroughBehaviors<TNotification>(
        INotificationPipelineBehavior<TNotification>[] behaviors,
        TNotification notification,
        CancellationToken cancellationToken)
        where TNotification : INotification
        => NotificationBehaviors.Run(behaviors, notification, ct => Deliver(notification, ct), cancellationToken);

    private Task Deliver<TNotification>(TNotification notification, CancellationToken cancellationToken)
        where TNotification : INotification
    {
        var subscriptions = routing.GetSubscriptions(notification.GetType());
        var byHand = routing.HandRegisteredHandlers<TNotification>(services, subscriptions);

        return (subscriptions.Count + byHand.Length) switch
        {
            0 => Task.CompletedTask,
            1 => subscriptions.Count == 1
                ? NotificationBehaviors.Start(subscriptions[0], services, notification, cancellationToken)
                : NotificationBehaviors.Start(byHand[0], notification, cancellationToken),
            _ => DeliverToManyAsync(subscriptions, byHand, notification, cancellationToken)
        };
    }

    // Subscriptions first, in the registry's order (by handler name), then the handlers registered by hand, in
    // registration order.
    private async Task DeliverToManyAsync<TNotification>(
        IReadOnlyList<NotificationSubscription> subscriptions,
        INotificationHandler<TNotification>[] byHand,
        TNotification notification,
        CancellationToken cancellationToken)
        where TNotification : INotification
    {
        var strategy = routing.PublishStrategy;
        if (strategy == PublishStrategy.Sequential)
        {
            // One at a time, stopping at the first failure.
            for (var i = 0; i < subscriptions.Count; i++)
                await subscriptions[i].Handle(services, notification, cancellationToken).ConfigureAwait(false);
            foreach (var handler in byHand)
                await (handler.Handle(notification, cancellationToken) ?? Task.CompletedTask).ConfigureAwait(false);
            return;
        }

        // Checked before any handler starts: the options validation rejects an undefined strategy at host start, but a
        // provider used without a host never runs it.
        if (strategy is not (PublishStrategy.Parallel or PublishStrategy.ParallelWhenAllAggregate))
            throw new InvalidOperationException($"'{strategy}' is not a defined {nameof(PublishStrategy)}.");

        var tasks = new Task[subscriptions.Count + byHand.Length];
        for (var i = 0; i < subscriptions.Count; i++)
            tasks[i] = NotificationBehaviors.Start(subscriptions[i], services, notification, cancellationToken);
        for (var i = 0; i < byHand.Length; i++)
            tasks[subscriptions.Count + i] = NotificationBehaviors.Start(byHand[i], notification, cancellationToken);

        var whenAll = Task.WhenAll(tasks);
        if (strategy == PublishStrategy.Parallel)
        {
            // The first failure surfaces (await's default); the siblings are observed through the WhenAll task.
            await whenAll.ConfigureAwait(false);
            return;
        }

        try
        {
            await whenAll.ConfigureAwait(false);
        }
        catch
        {
            // ParallelWhenAllAggregate: every failure surfaces, since await rethrows only the first. A single failure is
            // rethrown as itself, not wrapped.
            var failures = whenAll.Exception?.InnerExceptions;
            if (failures is { Count: > 1 })
                ExceptionDispatchInfo.Capture(new AggregateException(failures)).Throw();
            throw;
        }
    }
}
