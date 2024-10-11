using CQRSharp.Core.Notifications.Types;
using CQRSharp.Interfaces.Notifications;
using Microsoft.Extensions.Logging;

namespace CQRSharpSample.NotificationHandlers
{
    public sealed class QueryInitiatedNotificationHandler(ILogger<QueryInitiatedNotificationHandler> logger) : INotificationHandler<QueryInitiatedNotification<int>>
    {
        public Task Handle(QueryInitiatedNotification<int> notification, CancellationToken cancellationToken)
        {
            logger.LogInformation($"Query {notification.QueryName} initiated.");
            return Task.CompletedTask;
        }
    }
}