using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using CQRSharp.Core.Modules;

namespace CQRSharp.Core.Notifications;

/// <summary>
///     Every module's subscriptions and partition key selectors, merged once per provider. Which subscriptions a runtime
///     notification type reaches is computed on first use and cached, so a publish, and the storing of a notification in
///     the outbox, costs one dictionary lookup.
/// </summary>
internal sealed class NotificationSubscriptionRegistry : INotificationSubscriptionRegistry
{
    private readonly NotificationSubscription[] _all;
    private readonly ConcurrentDictionary<Type, NotificationSubscription[]> _byRuntimeType = new();
    private readonly FrozenDictionary<Type, Func<INotification, string?>> _partitionKeySelectors;

    public NotificationSubscriptionRegistry(IEnumerable<ICqrsModule> modules)
    {
        var materialized = modules as IReadOnlyCollection<ICqrsModule> ?? modules.ToArray();

        // Handler name first, so the order handlers run in, and a notification's messages are stored in, is the same in
        // every build and whichever modules declare them.
        _all = materialized
            .SelectMany(m => m.NotificationSubscriptions)
            .OrderBy(s => s.HandlerName, StringComparer.Ordinal)
            .ThenBy(s => s.NotificationType.FullName, StringComparer.Ordinal)
            .ToArray();

        // Last module wins, matching the route tables.
        var selectors = new Dictionary<Type, Func<INotification, string?>>();
        foreach (var module in materialized)
        foreach (var selector in module.PartitionKeySelectors)
            selectors[selector.Key] = selector.Value;

        _partitionKeySelectors = selectors.ToFrozenDictionary();
    }

    public IReadOnlyList<NotificationSubscription> Subscriptions => _all;

    public IReadOnlyList<NotificationSubscription> GetSubscriptions(Type notificationType)
    {
        ArgumentNullException.ThrowIfNull(notificationType);
        return _byRuntimeType.GetOrAdd(notificationType, static (type, all) =>
        {
            // One subscription per handler type: a handler declared for both a base type and a derived one is one handler
            // to its caller, so it runs once, for the nearest of its declared types, not once per declared type.
            Dictionary<Type, NotificationSubscription>? nearest = null;
            foreach (var subscription in all)
            {
                if (!subscription.NotificationType.IsAssignableFrom(type)) continue;
                nearest ??= new Dictionary<Type, NotificationSubscription>();
                if (!nearest.TryGetValue(subscription.HandlerType, out var current) ||
                    NotificationSpecificity.Compare(subscription.NotificationType, current.NotificationType, type) < 0)
                    nearest[subscription.HandlerType] = subscription;
            }

            if (nearest is null) return Array.Empty<NotificationSubscription>();

            // In the registry's order (by handler name, then type).
            var matches = new List<NotificationSubscription>(nearest.Count);
            foreach (var subscription in all)
                if (nearest.TryGetValue(subscription.HandlerType, out var chosen) && ReferenceEquals(chosen, subscription))
                    matches.Add(subscription);

            return matches.ToArray();
        }, _all);
    }

    public bool TryGetSubscription(Type notificationType, string handlerName, [NotNullWhen(true)] out NotificationSubscription? subscription)
    {
        ArgumentNullException.ThrowIfNull(handlerName);

        var candidates = GetSubscriptions(notificationType);
        for (var i = 0; i < candidates.Count; i++)
        {
            if (!string.Equals(candidates[i].HandlerName, handlerName, StringComparison.Ordinal)) continue;

            subscription = candidates[i];
            return true;
        }

        subscription = null;
        return false;
    }

    public string? GetPartitionKey(INotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // The interface is the explicit, computed override; the attribute is the declarative default.
        if (notification is IPartitionedNotification partitioned)
            return partitioned.PartitionKey;

        return _partitionKeySelectors.TryGetValue(notification.GetType(), out var selector) ? selector(notification) : null;
    }
}
