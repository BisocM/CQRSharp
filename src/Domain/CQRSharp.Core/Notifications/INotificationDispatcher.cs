using CQRSharp.Abstractions.Data.Interfaces.Notifications;

namespace CQRSharp.Core.Notifications;

/// <summary>
///     Responsible for dispatching notifications to registered handlers in an asynchronous manner.
/// </summary>
/// <remarks>
///     The NotificationDispatcher relies on an <see cref="IServiceProvider" /> to resolve instances
///     of <see cref="INotificationHandler{TNotification}" /> for the given notification type. It will
///     execute all handler's operations concurrently.
/// </remarks>
public interface INotificationDispatcher
{
    /// <summary>
    ///     Publishes a notification to all its associated handlers asynchronously.
    /// </summary>
    /// <typeparam name="TNotification">
    ///     The type of the notification being published, which must implement
    ///     <see cref="INotification" />.
    /// </typeparam>
    /// <param name="notification">The notification instance to be handled.</param>
    /// <param name="cancellationToken">An optional token to cancel the operation.</param>
    Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification;
}