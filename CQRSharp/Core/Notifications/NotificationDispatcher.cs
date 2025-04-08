using CQRSharp.Shared.Core.Data.Interfaces.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Notifications;

/// <summary>
///     Responsible for dispatching notifications to registered handlers in an asynchronous manner.
/// </summary>
/// <remarks>
///     The NotificationDispatcher relies on an <see cref="IServiceProvider" /> to resolve instances
///     of <see cref="INotificationHandler{TNotification}" /> for the given notification type. It will
///     execute all handler's operations concurrently.
/// </remarks>
public sealed class NotificationDispatcher(IServiceProvider serviceProvider)
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
    public async Task Publish<TNotification>(TNotification notification,
        CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        //Get all handlers for the notification.
        var handlers = serviceProvider.GetServices<INotificationHandler<TNotification>>();

        //Invoke all handlers concurrently.
        //FIXME: This might cause issues with control flow later.
        var tasks = handlers.Select(handler => handler.Handle(notification, cancellationToken));
        await Task.WhenAll(tasks);
    }
}