﻿using CQRSharp.Interfaces.Markers;
using CQRSharp.Interfaces.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Notifications
{
    public sealed class NotificationDispatcher(IServiceProvider serviceProvider)
    {
        public async Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
        {
            // Get all handlers for the notification.
            var handlers = serviceProvider.GetServices<INotificationHandler<TNotification>>();

            // Invoke all handlers concurrently.
            var tasks = handlers.Select(handler => handler.Handle(notification, cancellationToken));
            await Task.WhenAll(tasks);
        }
    }
}