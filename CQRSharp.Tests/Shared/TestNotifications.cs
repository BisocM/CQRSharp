using CQRSharp.Abstractions.Data.Interfaces.Notifications;

namespace CQRSharp.Tests.Shared;

/// <summary>
/// A basic notification for testing the notification dispatching system.
/// </summary>
public record TestNotification : INotification;