// CQRSharp.Sample/Notifications/UserCreatedNotification.cs

using CQRSharp.Abstractions.Attributes.Notifications;
using CQRSharp.Abstractions.Interfaces.Notifications;

namespace CQRSharp.Sample.Domain.Events;

public static class SampleNotificationNames
{
    public const string UserCreated = "sample.user.created";
}

[NotificationName(SampleNotificationNames.UserCreated)]
public class UserCreatedNotification(Guid userId, string name) : INotification
{
    // Parameterless constructor for deserialization
    public UserCreatedNotification() : this(Guid.Empty, string.Empty)
    {
    }

    public Guid UserId { get; set; } = userId;
    public string Name { get; set; } = name;
}
