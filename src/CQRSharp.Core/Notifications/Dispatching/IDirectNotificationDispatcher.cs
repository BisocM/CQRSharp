namespace CQRSharp.Core.Notifications;

/// <summary>
///     Publishes a notification in-process, straight to its handlers, bypassing the outbox. Resolve it from a DI scope
///     (it is scoped): the handlers and the notification pipeline behaviors are resolved from that scope. Both overloads
///     follow the same rule: a notification of runtime type R reaches every generated handler declared for R, a base type
///     or an interface of R, each once, plus the handlers registered by hand for R, and R's notification behaviors wrap
///     the fan-out.
/// </summary>
internal interface IDirectNotificationDispatcher
{
    /// <summary>
    ///     Publishes a notification held only as <see cref="INotification" /> (a domain-event list, a deserialized
    ///     message) exactly as if it had been published as its runtime type.
    /// </summary>
    /// <param name="notification">The notification object.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when every handler has.</returns>
    Task Publish(INotification notification, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Publishes a notification to its handlers immediately.
    /// </summary>
    /// <typeparam name="TNotification">The type the notification is published as; the delivery follows its runtime type.</typeparam>
    /// <param name="notification">The notification object.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when every handler has.</returns>
    Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification;
}
