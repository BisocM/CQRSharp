namespace CQRSharp;

/// <summary>
///     Provides a stable, application-defined name for a notification type — the opt-in that makes a notification
///     <b>durable through the outbox</b>. The source generator emits an AOT-safe serializer for every notification marked
///     with this attribute (a shape it cannot serialize is a build error, CQRGEN005), and the outbox stores it under this
///     name; a notification <b>without</b> it is dispatched
///     in-process, even when an outbox mode is enabled (the startup validator reports that as CQRCONF003). The name
///     identifies stored messages, so it must stay stable across refactors and be unique across notification types.
/// </summary>
/// <remarks>
///     A custom serializer registered with <c>AddNotificationSerializer&lt;T&gt;()</c> replaces the generated one, and
///     then it alone decides which notifications are durable and under which names: this attribute makes a notification
///     durable only while the generated serializer is in use.
/// </remarks>
/// <param name="name">A stable identifier for the notification type.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NotificationNameAttribute(string name) : Attribute
{
    /// <summary>
    ///     The stable identifier for the notification type.
    /// </summary>
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>
    ///     The name of the property whose value orders this notification's outbox deliveries
    ///     (<c>PartitionBy = nameof(OrderId)</c>). Deliveries that share the property's value — and a handler — are
    ///     delivered strictly in the order they were published; a <see langword="null" /> value leaves that delivery
    ///     unordered. The source generator reports CQRGEN011 when no such property exists. For a composed or computed
    ///     key implement <see cref="IPartitionedNotification" /> instead.
    /// </summary>
    /// <remarks>The relational outbox stores keys of at most 256 characters; derive a shorter key (a hash, say) for longer natural ones.</remarks>
    public string? PartitionBy { get; set; }
}
