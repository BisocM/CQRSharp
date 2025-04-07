using CQRSharp.Shared.Attributes;
using CQRSharp.Shared.Attributes.Requests;

namespace CQRSharp.Interfaces.Notifications;

/// <summary>
///     Marker interface for notifications.
/// </summary>
[RequestMarker(RequestKind.Notification)]
public interface INotification;