using System.Text;
using System.Text.Json;
using CQRSharp.Abstractions.Data.Interfaces.Notifications;

namespace CQRSharp.Sample.Notifications;

public class CustomJsonSerializer : INotificationSerializer
{
    private readonly SampleJsonContext _context = new(new JsonSerializerOptions { WriteIndented = true });

    public byte[] Serialize(INotification notification)
    {
        string jsonString = JsonSerializer.Serialize(notification, notification.GetType(), _context);
        return Encoding.UTF8.GetBytes(jsonString);
    }

    public INotification? Deserialize(string notificationName, byte[] payload)
    {
        Type? notificationType = Type.GetType(notificationName);
        if (notificationType is null) return null;
        
        var jsonString = Encoding.UTF8.GetString(payload);
        return JsonSerializer.Deserialize(jsonString, notificationType, _context) as INotification;
    }

    public string GetNotificationName(Type notificationType)
    {
        // Using AssemblyQualifiedName is more robust for deserialization across different projects.
        return notificationType.AssemblyQualifiedName!;
    }
}