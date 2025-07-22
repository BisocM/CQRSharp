using CQRSharp.Abstractions.Data.Attributes.Requests;

namespace CQRSharp.Abstractions.Data.Interfaces.Notifications;

/// <summary>
///     Marker interface for notifications.
/// </summary>
[RequestMarker(RequestKind.Notification)]
public interface INotification;