using System.Diagnostics.CodeAnalysis;
using System.Text;
using CQRSharp.Persistence;

namespace CQRSharp.Tests.Shared;

/// <summary>
///     A notification serializer that names one notification type and nothing else — the shape of a generated
///     serializer — so a test decides which notifications are durable without the generated one.
/// </summary>
/// <param name="name">The durable name it gives <typeparamref name="TNotification" />.</param>
public class SingleTypeNotificationSerializer<TNotification>(string name) : INotificationSerializer
    where TNotification : INotification, new()
{
    public bool TryGetNotificationName(Type notificationType, [NotNullWhen(true)] out string? notificationName)
    {
        notificationName = notificationType == typeof(TNotification) ? name : null;
        return notificationName is not null;
    }

    public byte[] Serialize(INotification notification) => Encoding.UTF8.GetBytes(name);

    public INotification? Deserialize(string notificationName, byte[] payload) => notificationName == name ? new TNotification() : null;
}
