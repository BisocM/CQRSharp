using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using CQRSharp.Persistence;

namespace CQRSharp.Core.Modules;

/// <summary>
///     The application's default <see cref="INotificationSerializer" />: every module's source-generated serializer behind
///     one. Which module names a notification type, and which type a stored name stands for, is decided once, when the
///     serializer is built, so serializing and reading back are one lookup each and always agree. When two modules give
///     different types the same name, the last registered module (the composition root's own) keeps it, the other type
///     is not durable, and the clash is reported as <c>CQRCONF010</c>. <c>AddNotificationSerializer&lt;T&gt;()</c>
///     replaces it outright.
/// </summary>
internal sealed class CompositeOutboxNotificationSerializer : INotificationSerializer
{
    private readonly FrozenDictionary<Type, (string Name, INotificationSerializer Serializer)> _byType;
    private readonly FrozenDictionary<string, (Type Type, INotificationSerializer Serializer)> _byName;

    public CompositeOutboxNotificationSerializer(IEnumerable<ICqrsModule> modules)
    {
        var byType = new Dictionary<Type, (string Name, INotificationSerializer Serializer)>();
        var byName = new Dictionary<string, (Type Type, INotificationSerializer Serializer)>(StringComparer.Ordinal);
        var claimants = new Dictionary<string, List<Type>>(StringComparer.Ordinal);

        foreach (var module in modules)
        {
            if (module.OutboxSerializer is not { } serializer) continue;

            // A module's generated serializer names exactly the [NotificationName] types the module declares, and every
            // one of them has a route there.
            foreach (var type in module.NotificationRoutes.Keys)
            {
                if (!serializer.TryGetNotificationName(type, out var name)) continue;

                byType[type] = (name, serializer);
                byName[name] = (type, serializer);

                if (!claimants.TryGetValue(name, out var types)) claimants[name] = types = [];
                if (!types.Contains(type)) types.Add(type);
            }
        }

        // A type that lost its name to another module's type is not durable: stored under the name, it would be read
        // back as the other type.
        foreach (var entry in byType.ToArray())
            if (byName[entry.Value.Name].Type != entry.Key)
                byType.Remove(entry.Key);

        _byType = byType.ToFrozenDictionary();
        _byName = byName.ToFrozenDictionary(StringComparer.Ordinal);
        Conflicts = claimants
            .Where(c => c.Value.Count > 1)
            .OrderBy(c => c.Key, StringComparer.Ordinal)
            .Select(c => new NameConflict(c.Key, c.Value.OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray()))
            .ToArray();
    }

    /// <summary>The names two or more modules give to different notification types.</summary>
    public IReadOnlyList<NameConflict> Conflicts { get; }

    public bool TryGetNotificationName(Type notificationType, [NotNullWhen(true)] out string? notificationName)
    {
        ArgumentNullException.ThrowIfNull(notificationType);

        if (_byType.TryGetValue(notificationType, out var entry))
        {
            notificationName = entry.Name;
            return true;
        }

        notificationName = null;
        return false;
    }

    public byte[] Serialize(INotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        return _byType.TryGetValue(notification.GetType(), out var entry)
            ? entry.Serializer.Serialize(notification)
            : throw new InvalidOperationException(
                $"Notification type '{notification.GetType().FullName}' has no generated outbox serializer. Mark it with " +
                "[NotificationName] (in a shape the source generator supports), or register your own serializer with " +
                "AddNotificationSerializer<T>(), which replaces the generated ones and must then name and serialize every " +
                "durable notification.");
    }

    public INotification? Deserialize(string notificationName, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(notificationName);
        ArgumentNullException.ThrowIfNull(payload);

        return _byName.TryGetValue(notificationName, out var entry) ? entry.Serializer.Deserialize(notificationName, payload) : null;
    }

    /// <summary>A stable name several notification types are given.</summary>
    /// <param name="Name">The name.</param>
    /// <param name="Types">The types given it, ordered by name.</param>
    internal sealed record NameConflict(string Name, IReadOnlyList<Type> Types);
}
