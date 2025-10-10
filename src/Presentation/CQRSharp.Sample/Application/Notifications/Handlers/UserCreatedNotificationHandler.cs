using CQRSharp.Abstractions.Data.Interfaces.Notifications;
using CQRSharp.Sample.Domain.Events;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Application.Notifications.Handlers;

public class UserCreatedNotificationHandler(ILogger<UserCreatedNotificationHandler> logger)
    : INotificationHandler<UserCreatedNotification>
{
    public Task Handle(UserCreatedNotification notification, CancellationToken cancellationToken)
    {
        logger.LogWarning("[NOTIFICATION HANDLER] New user created! Name: {Name}, ID: {Id}",
            notification.Name, notification.UserId);
        return Task.CompletedTask;
    }
}