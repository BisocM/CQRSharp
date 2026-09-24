using System.Collections.Frozen;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Notifications;

/// <summary>
///     Everything an in-process publish needs that is the same for every scope, built once per service provider: every
///     module's notification routes merged into one table, the subscription registry the outbox stores by (so both paths
///     reach the same handlers), the configured <see cref="PublishStrategy" />, whether a handler type can have been
///     registered by hand, and the provider's metrics.
/// </summary>
internal sealed class NotificationRouting
{
    private readonly FrozenDictionary<Type, NotificationRoute> _routes;
    private readonly INotificationSubscriptionRegistry? _subscriptions;
    private readonly IServiceProviderIsService? _isService;

    public NotificationRouting(
        IEnumerable<ICqrsModule> modules,
        INotificationSubscriptionRegistry? subscriptions,
        PublishStrategy publishStrategy,
        IServiceProviderIsService? isService,
        CqrsMetrics metrics)
    {
        // Every module's route for a type is the same Core object, so which one is kept does not matter.
        var routes = new Dictionary<Type, NotificationRoute>();
        foreach (var module in modules)
        foreach (var route in module.NotificationRoutes)
            routes[route.Key] = route.Value;

        _routes = routes.ToFrozenDictionary();
        _subscriptions = subscriptions;
        _isService = isService;
        PublishStrategy = publishStrategy;
        Metrics = metrics;
    }

    /// <summary>The provider's CQRSharp instruments, which count every in-process publish.</summary>
    public CqrsMetrics Metrics { get; }

    /// <summary>How a publish runs several handlers.</summary>
    public PublishStrategy PublishStrategy { get; }

    /// <summary>The concrete notification types some module has a route for.</summary>
    public IEnumerable<Type> RoutedTypes => _routes.Keys;

    /// <summary>The typed entry point of a runtime notification type, or <see langword="null" /> when no module knows it.</summary>
    public NotificationRoute? TryGetRoute(Type notificationType) => _routes.GetValueOrDefault(notificationType);

    /// <summary>The subscriptions a notification of this runtime type reaches.</summary>
    public IReadOnlyList<NotificationSubscription> GetSubscriptions(Type notificationType)
        => _subscriptions?.GetSubscriptions(notificationType) ?? Array.Empty<NotificationSubscription>();

    /// <summary>
    ///     Whether handlers can have been registered by hand under <paramref name="handlerService" />. The generator never
    ///     registers a notification handler under its interface, so for most types the container can prove there are none
    ///     and a publish skips resolving them; a container that cannot answer is asked.
    /// </summary>
    public bool MayHaveHandRegisteredHandlers(Type handlerService) => _isService?.IsService(handlerService) ?? true;

    /// <summary>
    ///     Whether the generator may have registered closed behaviors under <paramref name="behaviorService" /> (keyed by
    ///     <see cref="DiscoveredServices.Key" />); a container that cannot answer is asked.
    /// </summary>
    public bool MayHaveDiscoveredBehaviors(Type behaviorService)
        => _isService is not IServiceProviderIsKeyedService keyed || keyed.IsKeyedService(behaviorService, DiscoveredServices.Key);

    /// <summary>
    ///     The handlers registered by hand as <c>INotificationHandler&lt;TNotification&gt;</c> in <paramref name="services" />.
    ///     The generator registers its handlers only by their concrete type, so these are the application's own
    ///     registrations; a registration of a type the generator also discovered (an assembly scan, say) is left out, since
    ///     that handler is already delivered through its subscription.
    /// </summary>
    public INotificationHandler<TNotification>[] HandRegisteredHandlers<TNotification>(
        IServiceProvider services,
        IReadOnlyList<NotificationSubscription> subscriptions)
        where TNotification : INotification
    {
        if (!MayHaveHandRegisteredHandlers(typeof(INotificationHandler<TNotification>)))
            return [];

        var resolved = services.GetServices<INotificationHandler<TNotification>>();
        var handlers = resolved as INotificationHandler<TNotification>[] ?? resolved.ToArray();
        if (handlers.Length == 0 || subscriptions.Count == 0) return handlers;

        return Array.FindAll(handlers, handler =>
        {
            var handlerType = handler.GetType();
            for (var i = 0; i < subscriptions.Count; i++)
                if (subscriptions[i].HandlerType == handlerType)
                    return false;
            return true;
        });
    }
}
