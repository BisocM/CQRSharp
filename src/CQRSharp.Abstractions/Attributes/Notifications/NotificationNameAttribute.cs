namespace CQRSharp.Abstractions.Attributes.Notifications;

/// <summary>
///     Provides a stable, application-defined name for a notification type — the opt-in that makes a notification
///     <b>durable through the outbox</b>. A notification marked with this attribute can be serialized and persisted by
///     the outbox; a notification <b>without</b> it is always dispatched in-process, even when an outbox mode is enabled
///     (the startup validator reports that as CQRCONF003). The name (de)serializes the notification for durable storage,
///     so it must stay stable across refactors and be unique across notification types.
/// </summary>
/// <param name="name">A stable identifier for the notification type.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NotificationNameAttribute(string name) : Attribute
{
    /// <summary>
    ///     The stable identifier for the notification type.
    /// </summary>
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));
}