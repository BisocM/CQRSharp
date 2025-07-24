namespace CQRSharp.Abstractions.Data.Attributes.Notifications;

/// <summary>
/// Provides a stable, unique name for a notification, used for serialization.
/// This is required for all INotification types to ensure robust deserialization
/// and AoT compatibility.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NotificationNameAttribute(string name) : Attribute
{
    /// <summary>
    /// The name that is attributed to this notification. This must remain static, so that the OutboxProcessor's deserialization logic can persist through version changes.
    /// </summary>
    public string Name { get; } = name;
}