using CQRSharp.Core.Notifications.Types;
using CQRSharp.Interfaces.Notifications;
using Microsoft.Extensions.Logging;

namespace CQRSharpSample.NotificationHandlers
{
    public class QueryCompletedNotificationHandler(ILogger<QueryCompletedNotificationHandler> logger) : INotificationHandler<QueryCompletedNotification<int>>
    {
        public Task Handle(QueryCompletedNotification<int> notification, CancellationToken cancellationToken)
        {
            //Handle the notification
            logger.LogInformation($"Query '{notification.QueryName}' completed with result: {notification.Result}");
            return Task.CompletedTask;
        }
    }
}