// CQRSharp.Sample/Notifications/UserCreatedNotification.cs
using CQRSharp.Abstractions.Data.Interfaces.Notifications;

namespace CQRSharp.Sample.Notifications;

public class UserCreatedNotification(Guid userId, string name) : INotification
{
    // Parameterless constructor for deserialization
    public UserCreatedNotification() : this(Guid.Empty, string.Empty) { }

    public Guid UserId { get; set; } = userId;
    public string Name { get; set; } = name;
}