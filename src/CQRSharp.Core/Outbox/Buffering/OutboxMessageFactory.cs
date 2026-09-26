using System.Diagnostics;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Transports;
using CQRSharp.Persistence;
using CQRSharp.Transports;

namespace CQRSharp.Core.Outbox;

/// <summary>
///     Builds the durable <see cref="OutboxMessage" />s for a buffered notification: one per handler subscribed to it, and
///     one per notification transport that forwards it, stamped with its name, payload, recipient name, partition key,
///     timestamp and trace context. <see cref="OutboxWriter" /> stores what it builds.
/// </summary>
internal static class OutboxMessageFactory
{
    /// <summary>
    ///     Creates one pending <see cref="OutboxMessage" /> per (notification, subscribed handler), then one per
    ///     (notification, forwarding transport), all stamped with the current trace parent. The messages of one notification
    ///     share its name, payload, <see cref="OutboxMessage.NotificationId" />, creation time, due time and partition key; a
    ///     transport's message is addressed to the transport's name. A notification that no handler subscribes to and no
    ///     transport forwards produces no message.
    /// </summary>
    /// <param name="entries">The notifications drained from the scoped outbox, in publication order.</param>
    /// <param name="serializer">The serializer that provides each notification's stable name and payload.</param>
    /// <param name="subscriptions">The registry that knows which handlers subscribe to each notification and its partition key.</param>
    /// <param name="transports">
    ///     The transports that may forward the notifications, or <see langword="null" /> for messages meant for the local
    ///     handlers alone (a notification received through a transport is never sent back out).
    /// </param>
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
        NotificationTransportRegistry? transports,
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

            // Only a named notification can be forwarded: the name is what a transport routes and what the receiver reads
            // it back by.
            string? notificationName = null;
            INotificationTransport[] forwarding = [];
            if (transports is { IsEmpty: false } && serializer.TryGetNotificationName(notificationType, out notificationName))
                forwarding = transports.TransportsFor(notificationType, notificationName);

            if (subscribed.Count == 0 && forwarding.Length == 0) continue;

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
            if (notificationName is null)
            {
                if (!serializer.TryGetNotificationName(notificationType, out var named))
                    throw new InvalidOperationException(
                        $"Notification type '{notificationType.FullName}' cannot be stored in the outbox: the registered " +
                        $"{nameof(INotificationSerializer)} gives it no name. Mark it with [NotificationName], or, when a " +
                        "custom serializer is registered with AddNotificationSerializer<T>(), have that serializer name it.");
                notificationName = named;
            }

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

            // After the handlers' messages, with the same stamps: a scheduled notification is forwarded when it is due, and
            // a transport's messages are ordered per partition key among themselves, like a handler's.
            for (var t = 0; t < forwarding.Length; t++)
                messages.Add(new OutboxMessage(
                    Guid.NewGuid(),
                    notificationName,
                    forwarding[t].Name,
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
