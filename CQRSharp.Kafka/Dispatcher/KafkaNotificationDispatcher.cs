using System.Text.Json;
using Confluent.Kafka;
using CQRSharp.Core.Notifications;

namespace CQRSharp.Kafka.Dispatcher
{
    public sealed class KafkaNotificationDispatcher(
        IProducer<string, string> producer,
        IServiceProvider serviceProvider)
        : NotificationDispatcher(serviceProvider)
    {
        public override async Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        {
            var notificationName = typeof(TNotification).Name;
            var notificationData = JsonSerializer.Serialize(notification);

            await producer.ProduceAsync("events", new Message<string, string>
            {
                Key = notificationName,
                Value = notificationData
            }, cancellationToken);

            await base.Publish(notification, cancellationToken);
        }
    }
}