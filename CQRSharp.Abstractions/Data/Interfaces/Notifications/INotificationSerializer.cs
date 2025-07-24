namespace CQRSharp.Abstractions.Data.Interfaces.Notifications;

public interface INotificationSerializer
{
    /// <summary>
    /// Serializes the notification object into a string format.
    /// </summary>
    /// <param name="notification">The notification object to serialize.</param>
    /// <returns>A string representation of the notification.</returns>
    string Serialize(INotification notification);

    /// <summary>
    /// Deserializes the payload into a notification object.
    /// </summary>
    /// <param name="notificationName">The stable name of the notification.</param>
    /// <param name="payload">The string payload to deserialize.</param>
    /// <returns>The deserialized notification object, or null if deserialization fails.</returns>
    INotification? Deserialize(string notificationName, string payload);

    /// <summary>
    /// Gets the stable, unique name for a given notification type.
    /// </summary>
    /// <param name="notificationType">The type of the notification.</param>
    /// <returns>The unique name defined by the [NotificationName] attribute.</returns>
    string GetNotificationName(Type notificationType);
}