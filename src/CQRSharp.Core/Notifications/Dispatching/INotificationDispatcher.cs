namespace CQRSharp.Core.Notifications;

/// <summary>
///     Publishes notifications: into the outbox for deferred, durable delivery when the configured outbox mode routes a
///     notification there, otherwise in-process through <see cref="IDirectNotificationDispatcher" />. Either way the
///     notification reaches the same handlers.
/// </summary>
internal interface INotificationDispatcher
{
    /// <summary>
    ///     Publishes a notification to its handlers.
    /// </summary>
    /// <typeparam name="TNotification">
    ///     The type the notification is published as, which must implement <see cref="INotification" />; which handlers it
    ///     reaches follows its runtime type.
    /// </typeparam>
    /// <param name="notification">The notification instance to be handled.</param>
    /// <param name="cancellationToken">An optional token to cancel the operation.</param>
    /// <returns>A task that completes when the notification has been delivered in-process or stored.</returns>
    Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification;
}
