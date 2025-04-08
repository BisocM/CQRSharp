using CQRSharp.Shared.Attributes.Requests;

namespace CQRSharp.Shared.Core.Data.Interfaces.Notifications;

/// <summary>
///     Marker interface for notifications.
/// </summary>
[RequestMarker(RequestKind.Notification)]
public interface INotification;