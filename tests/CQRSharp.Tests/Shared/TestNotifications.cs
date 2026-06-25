using CQRSharp.Abstractions.Attributes.Notifications;
using CQRSharp.Abstractions.Interfaces.Notifications;

namespace CQRSharp.Tests.Shared;

/// <summary>
///     A basic notification for testing the notification dispatching system.
/// </summary>
[NotificationName("test.notification")]
public record TestNotification : INotification;