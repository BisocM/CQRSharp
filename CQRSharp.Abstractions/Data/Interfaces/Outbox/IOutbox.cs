using CQRSharp.Abstractions.Data.Interfaces.Notifications;

namespace CQRSharp.Abstractions.Data.Interfaces.Outbox;

/// <summary>
///     Defines a contract for a scoped, in-memory outbox used to collect notifications
///     within a single operation (e.g., a request).
///     Notifications added to this outbox are intended to be persisted atomically with the
///     main business logic by a behavior like the Unit of Work behavior.
/// </summary>
public interface IOutbox
{
    /// <summary>
    ///     Adds a notification to the outbox queue.
    /// </summary>
    /// <param name="notification">The notification to add.</param>
    void Add(INotification notification);

    /// <summary>
    ///     Retrieves all notifications currently in the outbox.
    /// </summary>
    /// <returns>A read-only list of notifications.</returns>
    IReadOnlyList<INotification> GetNotifications();
}