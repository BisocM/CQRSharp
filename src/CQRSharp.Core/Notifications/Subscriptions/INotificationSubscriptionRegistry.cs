using System.Diagnostics.CodeAnalysis;

namespace CQRSharp.Core.Notifications;

/// <summary>
///     The application's notification subscriptions and partition keys, merged from every source-generated module: which
///     handlers a published notification reaches (the same set in-process and through the outbox), how a claimed outbox
///     message finds the one handler it is addressed to, and the ordering key a stored notification gets.
/// </summary>
internal interface INotificationSubscriptionRegistry
{
    /// <summary>Every subscription of every module, in a deterministic order.</summary>
    IReadOnlyList<NotificationSubscription> Subscriptions { get; }

    /// <summary>
    ///     The subscriptions a notification of the given runtime type is delivered to: every handler declared for that
    ///     type, a base type or an interface it implements, each once, at the nearest of the types it is declared for
    ///     (the type itself, then base classes from the most derived up, then interfaces), ordered by handler name.
    /// </summary>
    /// <param name="notificationType">The notification's runtime type.</param>
    /// <returns>The matching subscriptions; empty when nothing handles the type.</returns>
    IReadOnlyList<NotificationSubscription> GetSubscriptions(Type notificationType);

    /// <summary>Finds the subscription an outbox message is addressed to.</summary>
    /// <param name="notificationType">The notification's runtime type.</param>
    /// <param name="handlerName">The <see cref="CQRSharp.Persistence.OutboxMessage.HandlerName" /> of the message.</param>
    /// <param name="subscription">The subscription, when found.</param>
    /// <returns><c>true</c> when a handler with that name subscribes to the type.</returns>
    bool TryGetSubscription(Type notificationType, string handlerName, [NotNullWhen(true)] out NotificationSubscription? subscription);

    /// <summary>
    ///     The ordering key of a notification: its <see cref="IPartitionedNotification.PartitionKey" /> when it
    ///     implements the interface, otherwise the value of the property its <c>[NotificationName(PartitionBy = ...)]</c>
    ///     names, otherwise <see langword="null" /> (unordered).
    /// </summary>
    /// <param name="notification">The notification being stored.</param>
    /// <returns>The key, or <see langword="null" /> for an unordered delivery.</returns>
    string? GetPartitionKey(INotification notification);
}
