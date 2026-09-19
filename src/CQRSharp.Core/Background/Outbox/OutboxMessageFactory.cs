using System.Diagnostics;
using CQRSharp.Pipelines;

namespace CQRSharp.Core.Background.Outbox;

/// <summary>
///     Builds the durable <see cref="OutboxMessage" /> for a buffered notification. Shared by every component that
///     persists the scoped outbox (the unit-of-work behaviors and the request-level flush) so they all stamp the same
///     name, payload, timestamp and trace context.
/// </summary>
public static class OutboxMessageFactory
{
    /// <summary>
    ///     Creates one pending <see cref="OutboxMessage" /> per notification, stamped with the current trace parent.
    /// </summary>
    /// <param name="notifications">The notifications drained from the scoped outbox.</param>
    /// <param name="serializer">The serializer that provides each notification's stable name and payload.</param>
    /// <param name="timeProvider">The clock used for the creation timestamp.</param>
    /// <returns>The messages, in the order the notifications were published.</returns>
    public static OutboxMessage[] Create(
        IReadOnlyList<INotification> notifications,
        INotificationSerializer serializer,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var traceParent = Activity.Current?.Id;
        var messages = new OutboxMessage[notifications.Count];
        for (var i = 0; i < messages.Length; i++)
        {
            var notification = notifications[i];
            messages[i] = new OutboxMessage(
                Guid.NewGuid(),
                serializer.GetNotificationName(notification.GetType()),
                serializer.Serialize(notification),
                timeProvider.GetUtcNow().UtcDateTime,
                OutboxMessageStatus.Pending,
                null,
                null,
                TraceParent: traceParent);
        }

        return messages;
    }
}
