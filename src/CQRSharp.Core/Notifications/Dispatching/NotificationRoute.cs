using System.ComponentModel;

namespace CQRSharp.Core.Notifications;

/// <summary>
///     The typed entry point of one concrete notification type: what lets the runtime resolve the notification pipeline
///     behaviors of a notification's runtime type when it only holds the notification as <see cref="INotification" />
///     (a domain-event list, an outbox message), without reflection. A source-generated module creates one per concrete
///     notification type its assembly declares or handles, through <see cref="For{TNotification}" />; everything a route
///     does is implemented here.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class NotificationRoute
{
    private protected NotificationRoute()
    {
    }

    /// <summary>The route of <typeparamref name="TNotification" />.</summary>
    /// <typeparam name="TNotification">A concrete notification type.</typeparam>
    public static NotificationRoute For<TNotification>() where TNotification : INotification => Route<TNotification>.Instance;

    /// <summary>Publishes the notification in-process, as its own type.</summary>
    internal abstract Task Publish(NotificationPublisher publisher, IServiceProvider services, INotification notification, CancellationToken cancellationToken);

    /// <summary>
    ///     The types of the handlers registered by hand for this notification type, which an outbox delivery never
    ///     reaches: the outbox delivers to subscriptions only.
    /// </summary>
    internal abstract Type[] HandRegisteredHandlerTypes(NotificationPublisher publisher, IServiceProvider services);

    /// <summary>Delivers the notification to one subscription, through the behaviors of the notification's type.</summary>
    internal abstract Task Deliver(
        NotificationPublisher publisher,
        IServiceProvider services,
        NotificationSubscription subscription,
        INotification notification,
        CancellationToken cancellationToken);

    private sealed class Route<TNotification> : NotificationRoute where TNotification : INotification
    {
        public static readonly Route<TNotification> Instance = new();

        internal override Task Publish(NotificationPublisher publisher, IServiceProvider services, INotification notification, CancellationToken cancellationToken)
            => publisher.PublishAsRuntimeType(services, (TNotification)notification, cancellationToken);

        internal override Type[] HandRegisteredHandlerTypes(NotificationPublisher publisher, IServiceProvider services)
            => publisher.HandRegisteredHandlerTypes<TNotification>(services);

        internal override Task Deliver(
            NotificationPublisher publisher,
            IServiceProvider services,
            NotificationSubscription subscription,
            INotification notification,
            CancellationToken cancellationToken)
            => publisher.DeliverAs(services, subscription, (TNotification)notification, cancellationToken);
    }
}
