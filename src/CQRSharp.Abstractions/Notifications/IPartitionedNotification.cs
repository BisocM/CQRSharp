namespace CQRSharp;

/// <summary>
///     Gives a durable notification an ordering key computed at runtime. Outbox deliveries that share a
///     <see cref="PartitionKey" /> (and a handler) are delivered strictly in the order they were published; deliveries
///     with different keys, or with no key, are independent of each other.
/// </summary>
/// <remarks>
///     For a key that is simply one property of the notification, prefer the declarative
///     <see cref="NotificationNameAttribute.PartitionBy" />, which the source generator checks at compile time. Implement
///     this interface when the key is composed or computed. When a notification has both, this interface wins.
/// </remarks>
public interface IPartitionedNotification
{
    /// <summary>
    ///     The ordering key, or <see langword="null" /> to deliver this notification without ordering. The relational
    ///     outbox stores keys of at most 256 characters; derive a shorter key (a hash, say) for longer natural ones.
    /// </summary>
    string? PartitionKey { get; }
}
