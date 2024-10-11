using CQRSharp.Interfaces.Markers;

namespace CQRSharp.Interfaces.Notifications
{
    /// <summary>
    /// Interface for handling notifications.
    /// </summary>
    /// <typeparam name="TNotification">The type of the notification.</typeparam>
    public interface INotificationHandler<in TNotification> where TNotification : INotification
    {
        /// <summary>
        /// Handles the notification.
        /// </summary>
        /// <param name="notification">The notification to handle.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task Handle(TNotification notification, CancellationToken cancellationToken);
    }
}