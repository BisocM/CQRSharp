using CQRSharp.Shared.Data.Attributes.Requests;

namespace CQRSharp.Shared.Data.Interfaces.Notifications;

/// <summary>
///     Marker interface for notifications.
/// </summary>
[RequestMarker(RequestKind.Notification)]
public interface INotification;