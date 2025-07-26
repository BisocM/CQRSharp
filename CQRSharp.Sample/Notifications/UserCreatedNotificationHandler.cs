using CQRSharp.Abstractions.Data.Interfaces.Notifications;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Notifications;

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