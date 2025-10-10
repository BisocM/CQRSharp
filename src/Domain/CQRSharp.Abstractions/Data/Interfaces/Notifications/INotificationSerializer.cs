namespace CQRSharp.Abstractions.Data.Interfaces.Notifications;

/// <summary>
///     Defines the contract for serializing and deserializing notification objects for durable storage, such as in an outbox pattern.
/// </summary>
public interface INotificationSerializer
{
    /// <summary>
    ///     Serializes the notification object into a byte array.
    /// </summary>
    /// <param name="notification">The notification object to serialize.</param>
    /// <returns>A byte array representation of the notification.</returns>
    byte[] Serialize(INotification notification);

    /// <summary>
    ///     Deserializes the payload from a byte array into a notification object.
    /// </summary>
    /// <param name="notificationName">The stable name of the notification, used to identify the correct type.</param>
    /// <param name="payload">The byte array payload to deserialize.</param>
    /// <returns>The deserialized notification object, or null if the notification name is unknown or deserialization fails.</returns>
    INotification? Deserialize(string notificationName, byte[] payload);

    /// <summary>
    ///     Gets the stable, unique name for a given notification type, typically from a `[NotificationName]` attribute.
    /// </summary>
    /// <param name="notificationType">The type of the notification.</param>
    /// <returns>A unique string identifier for the notification type.</returns>
    string GetNotificationName(Type notificationType);
}