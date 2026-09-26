
namespace CQRSharp;

/// <summary>
///     Handles notifications of type <typeparamref name="TNotification" />. A handler the source generator discovers
///     receives every published notification whose runtime type is, derives from or implements
///     <typeparamref name="TNotification" />, once, whether it is dispatched in-process or delivered through the outbox.
/// </summary>
/// <typeparam name="TNotification">The type of the notification.</typeparam>
public interface INotificationHandler<in TNotification> where TNotification : INotification
{
    /// <summary>
    ///     Handles the notification.
    /// </summary>
    /// <param name="notification">The notification to handle.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task Handle(TNotification notification, CancellationToken cancellationToken);
}