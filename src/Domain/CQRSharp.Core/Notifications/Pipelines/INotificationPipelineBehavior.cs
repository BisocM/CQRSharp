using CQRSharp.Abstractions.Interfaces.Notifications;

namespace CQRSharp.Core.Notifications.Pipelines;

/// <summary>
///     Defines an interface for notification pipeline behaviors that can be applied globally to all notifications.
/// </summary>
/// <typeparam name="TNotification">The type of the notification.</typeparam>
public interface INotificationPipelineBehavior<in TNotification>
    where TNotification : INotification
{
    /// <summary>
    ///     Handles the notification by invoking the next behavior in the pipeline or the final notification dispatch.
    /// </summary>
    /// <param name="notification">The notification being published.</param>
    /// <param name="next">The next delegate to be invoked.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    Task Handle(
        TNotification notification,
        Func<CancellationToken, Task> next,
        CancellationToken cancellationToken);
}