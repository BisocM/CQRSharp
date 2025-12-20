using CQRSharp.Abstractions.Data.Attributes.Notifications;
using CQRSharp.Abstractions.Data.Interfaces.Notifications;

namespace CQRSharp.Tests.Shared;

/// <summary>
///     A basic notification for testing the notification dispatching system.
/// </summary>
[NotificationName("test.notification")]
public record TestNotification : INotification;
