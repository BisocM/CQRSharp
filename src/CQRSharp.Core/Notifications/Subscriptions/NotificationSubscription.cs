using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Notifications;

/// <summary>
///     One handler's subscription to one notification type, as the source generator discovered it at compile time. The
///     subscriptions are what a published notification is delivered to: in-process, a publish runs every subscription the
///     notification's runtime type matches, and through the outbox one message is stored per matching subscription and
///     delivered to that subscription alone, which is what gives every handler its own attempts, back-off and dead letter.
///     Public because <c>ICqrsModule</c> hands them to the runtime; only generated modules create one.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class NotificationSubscription
{
    private protected NotificationSubscription(Type notificationType, Type handlerType, string handlerName)
    {
        if (string.IsNullOrWhiteSpace(handlerName))
            throw new ArgumentException("A subscription needs a non-empty handler name.", nameof(handlerName));

        NotificationType = notificationType;
        HandlerType = handlerType;
        HandlerName = handlerName;
    }

    /// <summary>
    ///     The subscription of <typeparamref name="THandler" /> to <typeparamref name="TNotification" />. Called by
    ///     generated modules, once per handler and handled notification type; the handler is resolved from the delivering
    ///     scope by its concrete type.
    /// </summary>
    /// <param name="handlerName">The handler's stable name; what an outbox message is addressed to.</param>
    /// <typeparam name="THandler">The concrete handler type.</typeparam>
    /// <typeparam name="TNotification">The notification type the handler is declared for.</typeparam>
    /// <returns>The subscription.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="handlerName" /> is empty or whitespace.</exception>
    public static NotificationSubscription For<THandler, TNotification>(string handlerName)
        where THandler : INotificationHandler<TNotification>
        where TNotification : INotification
        => new Subscription<THandler, TNotification>(handlerName);

    /// <summary>
    ///     The notification type the handler is declared for (the <c>TNotification</c> of its
    ///     <c>INotificationHandler&lt;TNotification&gt;</c>). A handler declared for a base type or interface subscribes to
    ///     every notification assignable to it.
    /// </summary>
    public Type NotificationType { get; }

    /// <summary>The concrete handler type.</summary>
    public Type HandlerType { get; }

    /// <summary>
    ///     The handler's stable name: the value of its <c>[NotificationHandlerName]</c>, or the type's namespace-qualified
    ///     name. Outbox messages are addressed to it, so it must not change while messages addressed to it may still be stored.
    /// </summary>
    public string HandlerName { get; }

    /// <summary>
    ///     Delivers a notification to this handler alone, in the given scope, wrapped by the notification pipeline
    ///     behaviors of the notification's runtime type: the outbox's per-handler delivery.
    /// </summary>
    /// <param name="services">The scope to resolve the handler and the behaviors from.</param>
    /// <param name="notification">The notification; assignable to <see cref="NotificationType" />.</param>
    /// <param name="cancellationToken">A token to cancel the delivery.</param>
    /// <returns>The delivery's task.</returns>
    internal Task Invoke(IServiceProvider services, INotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(notification);

        var routing = services.GetService<NotificationRouting>();
        return routing?.TryGetRoute(notification.GetType()) is { } route
            ? route.Deliver(services, routing, notification, this, cancellationToken)
            : DeliverAsDeclared(services, routing, notification, cancellationToken);
    }

    /// <summary>Runs the handler, without behaviors: one step of a delivery or of an in-process fan-out.</summary>
    internal abstract Task Handle(IServiceProvider services, INotification notification, CancellationToken cancellationToken);

    /// <summary>
    ///     Delivers through the behaviors of the handler's declared type, for a runtime type no module has a route for
    ///     (declared and handled only where the generator does not run).
    /// </summary>
    internal abstract Task DeliverAsDeclared(IServiceProvider services, NotificationRouting? routing, INotification notification, CancellationToken cancellationToken);

    /// <inheritdoc />
    public override string ToString() => $"{HandlerName} ({NotificationType.Name})";

    private sealed class Subscription<THandler, TNotification>(string handlerName)
        : NotificationSubscription(typeof(TNotification), typeof(THandler), handlerName)
        where THandler : INotificationHandler<TNotification>
        where TNotification : INotification
    {
        internal override Task Handle(IServiceProvider services, INotification notification, CancellationToken cancellationToken)
            => services.GetRequiredService<THandler>().Handle((TNotification)notification, cancellationToken) ?? Task.CompletedTask;

        internal override Task DeliverAsDeclared(IServiceProvider services, NotificationRouting? routing, INotification notification, CancellationToken cancellationToken)
        {
            var behaviors = NotificationBehaviors.Resolve<TNotification>(services, routing);
            return behaviors.Length == 0
                ? NotificationBehaviors.Start(this, services, notification, cancellationToken)
                : NotificationBehaviors.Run(behaviors, (TNotification)notification,
                    ct => NotificationBehaviors.Start(this, services, notification, ct), cancellationToken);
        }
    }
}
