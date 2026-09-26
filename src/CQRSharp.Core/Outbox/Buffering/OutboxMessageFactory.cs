using System.Diagnostics;
using CQRSharp.Core.Notifications;
using CQRSharp.Persistence;

namespace CQRSharp.Core.Outbox;

/// <summary>
///     Builds the durable <see cref="OutboxMessage" />s for a buffered notification: one per handler subscribed to it,
///     stamped with its name, payload, handler name, partition key, timestamp and trace context. <see cref="OutboxWriter" />
///     stores what it builds.
/// </summary>
internal static class OutboxMessageFactory
{
    /// <summary>
    ///     Creates one pending <see cref="OutboxMessage" /> per (notification, subscribed handler), stamped with the
    ///     current trace parent. A notification nothing subscribes to produces no message.
    /// </summary>
    /// <param name="notifications">The notifications drained from the scoped outbox, in publication order.</param>
    /// <param name="serializer">The serializer that provides each notification's stable name and payload.</param>
    /// <param name="subscriptions">The registry that knows which handlers subscribe to each notification and its partition key.</param>
    /// <param name="timeProvider">The clock used for the creation timestamp.</param>
    /// <returns>
    ///     The messages, in publication order. Within one call <see cref="OutboxMessage.CreatedAt" /> is strictly
    ///     increasing from notification to notification (the messages of one notification share a timestamp), so two
    ///     notifications published in the same clock tick are still stored — and delivered — in publication order.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    ///     A notification that some handler subscribes to has a type <paramref name="serializer" /> does not name.
    /// </exception>
    public static OutboxMessage[] Create(
        IReadOnlyList<INotification> notifications,
        INotificationSerializer serializer,
        INotificationSubscriptionRegistry subscriptions,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (notifications.Count == 0) return Array.Empty<OutboxMessage>();

        var traceParent = Activity.Current?.Id;
        var messages = new List<OutboxMessage>(notifications.Count);
        var previousCreatedAt = DateTime.MinValue;

        for (var i = 0; i < notifications.Count; i++)
        {
            var notification = notifications[i];
            var notificationType = notification.GetType();
            var subscribed = subscriptions.GetSubscriptions(notificationType);
            if (subscribed.Count == 0) continue;

            // CreatedAt is the primary ordering key, so it must not tie between two notifications of one batch.
            var createdAt = timeProvider.GetUtcNow().UtcDateTime;
            if (createdAt <= previousCreatedAt) createdAt = previousCreatedAt.AddTicks(1);
            previousCreatedAt = createdAt;

            // The dispatcher routes only named notifications here; a direct caller may still pass one without a name, and
            // nothing could ever read its message back.
            if (!serializer.TryGetNotificationName(notificationType, out var notificationName))
                throw new InvalidOperationException(
                    $"Notification type '{notificationType.FullName}' cannot be stored in the outbox: the registered " +
                    $"{nameof(INotificationSerializer)} gives it no name. Mark it with [NotificationName], or, when a " +
                    "custom serializer is registered with AddNotificationSerializer<T>(), have that serializer name it.");

            var payload = serializer.Serialize(notification);
            var partitionKey = subscriptions.GetPartitionKey(notification);
            var notificationId = Guid.NewGuid();

            for (var s = 0; s < subscribed.Count; s++)
                messages.Add(new OutboxMessage(
                    Guid.NewGuid(),
                    notificationName,
                    subscribed[s].HandlerName,
                    payload,
                    createdAt,
                    OutboxMessageStatus.Pending,
                    null,
                    null,
                    TraceParent: traceParent,
                    PartitionKey: partitionKey,
                    NotificationId: notificationId));
        }

        return messages.ToArray();
    }
}
