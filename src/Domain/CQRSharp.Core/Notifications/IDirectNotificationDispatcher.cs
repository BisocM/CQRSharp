using CQRSharp.Abstractions.Data.Interfaces.Notifications;

namespace CQRSharp.Core.Notifications;

/// <summary>
///     Defines a contract for dispatching notifications directly and immediately to their handlers.
///     This dispatcher bypasses any outbox mechanism and is used for in-process notifications.
/// </summary>
public interface IDirectNotificationDispatcher
{
    /// <summary>
    ///     Publishes a notification to all its registered handlers immediately, using the notification's runtime type.
    ///     This overload exists for scenarios where the compile-time type is only <see cref="INotification" />
    ///     (e.g., outbox deserialization).
    /// </summary>
    /// <param name="notification">The notification object.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous publish operation. The task completes when all handlers have been awaited.</returns>
    Task Publish(INotification notification, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Publishes a notification to all its registered handlers immediately.
    /// </summary>
    /// <typeparam name="TNotification">The type of notification being published.</typeparam>
    /// <param name="notification">The notification object.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous publish operation. The task completes when all handlers have been awaited.</returns>
    Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification;
}
