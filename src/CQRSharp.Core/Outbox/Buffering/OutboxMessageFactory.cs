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
    /// <param name="entries">The notifications drained from the scoped outbox, in publication order.</param>
    /// <param name="serializer">The serializer that provides each notification's stable name and payload.</param>
    /// <param name="subscriptions">The registry that knows which handlers subscribe to each notification and its partition key.</param>
    /// <param name="timeProvider">The clock used for the creation timestamp.</param>
    /// <returns>
    ///     The messages, in publication order. Within one call <see cref="OutboxMessage.CreatedAt" /> is strictly
    ///     increasing from notification to notification (the messages of one notification share a timestamp), so two
    ///     notifications published in the same clock tick are still stored — and delivered — in publication order. A
    ///     scheduled notification whose due time is still ahead is the exception: its messages take the due time as both
    ///     their <see cref="OutboxMessage.CreatedAt" /> and their <see cref="OutboxMessage.NextRetryAt" />, so they are
    ///     ordered, and claimable, from then on. One whose due time has already passed is stored as if it had been
    ///     published now: it cannot take a place in the order before messages that may already have been delivered.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    ///     A notification that some handler subscribes to has a type <paramref name="serializer" /> does not name.
    /// </exception>
    public static OutboxMessage[] Create(
        IReadOnlyList<OutboxEntry> entries,
        INotificationSerializer serializer,
        INotificationSubscriptionRegistry subscriptions,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (entries.Count == 0) return Array.Empty<OutboxMessage>();

        var traceParent = Activity.Current?.Id;
        var messages = new List<OutboxMessage>(entries.Count);
        var previousCreatedAt = DateTime.MinValue;

        for (var i = 0; i < entries.Count; i++)
        {
            var (notification, dueAt) = entries[i];
            var notificationType = notification.GetType();
            var subscribed = subscriptions.GetSubscriptions(notificationType);
            if (subscribed.Count == 0) continue;

            var now = timeProvider.GetUtcNow().UtcDateTime;
            DateTime createdAt;
            DateTime? notBefore = null;
            if (dueAt is { } due && due > now)
            {
                // Its place in the order is its due time; the stamps of the notifications around it are unaffected.
                createdAt = due;
                notBefore = due;
            }
            else
            {
                // CreatedAt is the primary ordering key, so it must not tie between two notifications of one batch.
                createdAt = now <= previousCreatedAt ? previousCreatedAt.AddTicks(1) : now;
                previousCreatedAt = createdAt;
            }

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
                    NextRetryAt: notBefore,
                    TraceParent: traceParent,
                    PartitionKey: partitionKey,
                    NotificationId: notificationId));
        }

        return messages.ToArray();
    }
}
