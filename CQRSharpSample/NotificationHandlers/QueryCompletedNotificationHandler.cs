using CQRSharp.Core.Notifications.Types;
using CQRSharp.Interfaces.Notifications;

namespace CQRSharpSample.NotificationHandlers
{
    public class QueryCompletedNotificationHandler : INotificationHandler<QueryCompletedNotification>
    {
        public Task Handle(QueryCompletedNotification notification, CancellationToken cancellationToken)
        {
            //Handle the notification
            Console.WriteLine($"Query '{notification.QueryName}' completed with result: {notification.Result}");
            return Task.CompletedTask;
        }
    }
}