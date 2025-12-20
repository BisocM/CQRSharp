using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Reflection;
using CQRSharp.Abstractions.Data.Attributes.Notifications;
using CQRSharp.Abstractions.Data.Interfaces.Notifications;
using CQRSharp.Sample.Domain.Events;

namespace CQRSharp.Sample.Infrastructure.Serialization;

public sealed class CustomJsonSerializer : INotificationSerializer
{
    private readonly SampleJsonContext _context = new(new JsonSerializerOptions { WriteIndented = true });

    private static readonly FrozenDictionary<string, Type> NotificationTypes =
        new Dictionary<string, Type>(StringComparer.Ordinal)
            {
                { GetStableName(typeof(UserCreatedNotification)), typeof(UserCreatedNotification) }
            }
            .ToFrozenDictionary(StringComparer.Ordinal);

    public byte[] Serialize(INotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var jsonString = JsonSerializer.Serialize(notification, notification.GetType(), _context);
        return Encoding.UTF8.GetBytes(jsonString);
    }

    public INotification? Deserialize(string notificationName, byte[] payload)
    {
        if (string.IsNullOrWhiteSpace(notificationName)) return null;
        if (!NotificationTypes.TryGetValue(notificationName, out var notificationType)) return null;

        var jsonString = Encoding.UTF8.GetString(payload);
        return JsonSerializer.Deserialize(jsonString, notificationType, _context) as INotification;
    }

    public string GetNotificationName(Type notificationType)
    {
        ArgumentNullException.ThrowIfNull(notificationType);
        return GetStableName(notificationType);
    }

    private static string GetStableName(Type notificationType)
    {
        var attribute = notificationType.GetCustomAttribute<NotificationNameAttribute>();
        if (attribute is not null) return attribute.Name;
        return notificationType.FullName ?? notificationType.Name;
    }
}
