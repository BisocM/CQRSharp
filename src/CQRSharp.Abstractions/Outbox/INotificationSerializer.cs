using System.Diagnostics.CodeAnalysis;

namespace CQRSharp.Persistence;

/// <summary>
///     Names, serializes and deserializes notifications for the outbox. An application has exactly one: by default the
///     source-generated serializer, which covers the <see cref="NotificationNameAttribute" /> notifications of every
///     CQRSharp module, or a custom one registered with <c>AddNotificationSerializer&lt;T&gt;()</c>, which replaces it
///     entirely. The registered serializer alone decides which notifications are durable: while an outbox mode is active,
///     a published notification it names (<see cref="TryGetNotificationName" />) is stored in the outbox under that name,
///     and one it does not name is dispatched in-process.
/// </summary>
public interface INotificationSerializer
{
    /// <summary>
    ///     Gets the stable name notifications of a type are stored under, which is also what makes them durable. The name
    ///     is handed back to <see cref="Deserialize" /> later, possibly by another process or a later version of the
    ///     application, so it must stay stable across refactors and be unique across notification types.
    /// </summary>
    /// <param name="notificationType">The runtime type of the published notification.</param>
    /// <param name="notificationName">The stable name, when the method returns <see langword="true" />.</param>
    /// <returns>
    ///     <see langword="true" /> when notifications of <paramref name="notificationType" /> are durable;
    ///     <see langword="false" /> when they are not, in which case they are dispatched in-process.
    /// </returns>
    bool TryGetNotificationName(Type notificationType, [NotNullWhen(true)] out string? notificationName);

    /// <summary>
    ///     Serializes a notification into the payload stored with its outbox messages. The outbox only calls this for a
    ///     notification whose type <see cref="TryGetNotificationName" /> names.
    /// </summary>
    /// <param name="notification">The notification to serialize.</param>
    /// <returns>The payload.</returns>
    byte[] Serialize(INotification notification);

    /// <summary>
    ///     Restores a stored notification from the name it was stored under and its payload.
    /// </summary>
    /// <remarks>
    ///     A payload that cannot be read must throw <c>System.Text.Json.JsonException</c>: no retry can fix it, so the
    ///     outbox processor dead-letters that message at once. Any other exception counts as a failed delivery attempt and
    ///     is retried, with back-off, until the attempts run out. A <see langword="null" /> result says this application
    ///     instance does not know the name; since another instance of a fleet running mixed versions may, the processor
    ///     defers the message (without counting an attempt) until its unknown-recipient grace period has passed, and
    ///     only then dead-letters it.
    /// </remarks>
    /// <param name="notificationName">The name the notification was stored under (see <see cref="TryGetNotificationName" />).</param>
    /// <param name="payload">The payload <see cref="Serialize" /> produced.</param>
    /// <returns>
    ///     The notification, or <see langword="null" /> when this serializer does not know
    ///     <paramref name="notificationName" />.
    /// </returns>
    INotification? Deserialize(string notificationName, byte[] payload);
}
