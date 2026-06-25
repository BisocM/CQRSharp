using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Sample.Domain.Events;
using CQRSharp.Sample.Infrastructure.SelfTest;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Application.Notifications.Handlers;

public class UserCreatedNotificationHandler(
    ILogger<UserCreatedNotificationHandler> logger,
    SampleDiagnostics diagnostics)
    : INotificationHandler<UserCreatedNotification>
{
    public Task Handle(UserCreatedNotification notification, CancellationToken cancellationToken)
    {
        diagnostics.RecordUserCreated(notification);
        logger.LogWarning("[NOTIFICATION HANDLER] New user created! Name: {Name}, ID: {Id}",
            notification.Name, notification.UserId);
        return Task.CompletedTask;
    }
}