namespace CQRSharp.Abstractions.Attributes.Notifications;

/// <summary>
///     Provides a stable, application-defined name for a notification type.
///     This name is intended for durable storage (e.g., outbox) and should remain stable across refactors.
/// </summary>
/// <param name="name">A stable identifier for the notification type.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class NotificationNameAttribute(string name) : Attribute
{
    /// <summary>
    ///     The stable identifier for the notification type.
    /// </summary>
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));
}

