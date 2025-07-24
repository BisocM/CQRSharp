using CQRSharp.Abstractions.Data.Attributes.Notifications;
using CQRSharp.Abstractions.Data.Interfaces.Notifications;

namespace CQRSharp.Sample.Notifications;

[NotificationName("sample.test_notification")]
public sealed class TestNotification(string Message, DateTime Timestamp) : INotification;