using CQRSharp.Shared.Attributes;

namespace CQRSharp.Interfaces.Notifications;

/// <summary>
///     Marker interface for notifications.
/// </summary>
[RequestMarker(RequestKind.Notification)]
public interface INotification;