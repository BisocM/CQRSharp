using System.Diagnostics.CodeAnalysis;
using CQRSharp.Core.Notifications;

namespace CQRSharp.Tests.Shared;

/// <summary>
///     A subscription registry over the subscriptions a test lists, standing in for the generated one: a
///     notification's subscriptions are those declared for its type or a type it derives from, found by handler name,
///     and no notification has a partition key.
/// </summary>
internal class FakeSubscriptionRegistry(params NotificationSubscription[] subscriptions) : INotificationSubscriptionRegistry
{
    private readonly List<NotificationSubscription> _subscriptions = [.. subscriptions];

    /// <summary>An empty registry, as the container builds it for a registration by type.</summary>
    public FakeSubscriptionRegistry() : this([])
    {
    }

    public IReadOnlyList<NotificationSubscription> Subscriptions => _subscriptions;

    /// <summary>Adds a subscription, for a test that builds the registry up as it goes.</summary>
    public void Add(NotificationSubscription subscription) => _subscriptions.Add(subscription);

    public IReadOnlyList<NotificationSubscription> GetSubscriptions(Type notificationType)
        => _subscriptions.Where(s => s.NotificationType.IsAssignableFrom(notificationType)).ToArray();

    public bool TryGetSubscription(Type notificationType, string handlerName, [NotNullWhen(true)] out NotificationSubscription? subscription)
    {
        subscription = GetSubscriptions(notificationType).FirstOrDefault(s => s.HandlerName == handlerName);
        return subscription is not null;
    }

    public string? GetPartitionKey(INotification notification) => null;
}
