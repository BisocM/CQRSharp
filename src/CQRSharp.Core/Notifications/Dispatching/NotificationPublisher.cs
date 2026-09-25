using System.Collections.Frozen;
using System.Runtime.ExceptionServices;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Modules;
using CQRSharp.Core.Outbox;
using CQRSharp.Core.Pipelines;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Notifications;

/// <summary>
///     Publishes notifications for one service provider: a singleton that owns everything a publish needs that is the
///     same in every scope (every module's notification routes, the subscription registry, the publish strategy, the
///     outbox mode, and a <see cref="NotificationPlan{TNotification}" /> per notification type), and is handed the scope to
///     resolve handlers and behaviors from on each call. A scope therefore costs a publish nothing to set up.
/// </summary>
/// <remarks>
///     <para>
///         In-process delivery follows one rule however a notification was published. A notification of runtime type R
///         reaches every subscription the <see cref="INotificationSubscriptionRegistry" /> gives R (every generated
///         handler declared for R, a base type or an interface of R, each once), plus the handlers registered by hand for
///         the type it is delivered as, and the notification pipeline behaviors of that type wrap the whole fan-out once.
///         The type it is delivered as is R when a module has a route for R, so publishing a domain event as
///         <see cref="INotification" /> behaves exactly like publishing it as itself; otherwise it is the type it was
///         published as.
///     </para>
///     <para>
///         An outbox delivery runs one subscription through the behaviors of the notification's runtime type, or of the
///         subscription's declared type when no module has a route for the runtime type.
///     </para>
/// </remarks>
internal sealed class NotificationPublisher : IDisposable
{
    private readonly FrozenDictionary<Type, NotificationRoute> _routes;
    private readonly INotificationSubscriptionRegistry? _subscriptions;
    private readonly ProviderPlanCache _plans = new();
    private readonly ProviderRegistrations _registrations;
    private readonly PublishStrategy _publishStrategy;
    private readonly OutboxMode _outboxMode;
    private readonly CqrsMetrics? _metrics;
    private readonly ILogger _configurationLogger;

    /// <param name="rootProvider">The provider's root, which the plans ask about its registrations.</param>
    /// <param name="modules">The composed modules, whose notification routes are merged into one table.</param>
    /// <param name="subscriptions">The subscription registry the outbox stores by, so both paths reach the same handlers.</param>
    /// <param name="options">How a publish runs several handlers.</param>
    /// <param name="outboxOptions">Whether, and when, a publish goes to the outbox.</param>
    /// <param name="metrics">The provider's instruments, which count every in-process publish.</param>
    /// <param name="configurationLogger">Where a configuration warning met at a publish is logged.</param>
    public NotificationPublisher(
        IServiceProvider rootProvider,
        IEnumerable<ICqrsModule> modules,
        INotificationSubscriptionRegistry? subscriptions,
        NotificationOptions options,
        OutboxOptions outboxOptions,
        CqrsMetrics? metrics,
        ILogger configurationLogger)
    {
        // Every module's route for a type is the same Core object, so which one is kept does not matter.
        var routes = new Dictionary<Type, NotificationRoute>();
        foreach (var module in modules)
        foreach (var route in module.NotificationRoutes)
            routes[route.Key] = route.Value;

        _routes = routes.ToFrozenDictionary();
        _subscriptions = subscriptions;
        _registrations = new ProviderRegistrations(rootProvider);
        _publishStrategy = options.PublishStrategy;
        _outboxMode = outboxOptions.Mode;
        _metrics = metrics;
        _configurationLogger = configurationLogger;
    }

    /// <summary>The concrete notification types some module has a route for.</summary>
    public IEnumerable<Type> RoutedTypes => _routes.Keys;

    /// <summary>Builds the publisher of <paramref name="rootProvider" /> from its registrations.</summary>
    public static NotificationPublisher Create(IServiceProvider rootProvider)
        => new(
            rootProvider,
            rootProvider.GetServices<ICqrsModule>(),
            rootProvider.GetService<INotificationSubscriptionRegistry>(),
            rootProvider.GetService<IOptions<NotificationOptions>>()?.Value ?? new NotificationOptions(),
            rootProvider.GetService<IOptions<OutboxOptions>>()?.Value ?? new OutboxOptions(),
            rootProvider.GetService<CqrsMetrics>(),
            rootProvider.GetService<ILoggerFactory>()?.CreateLogger(CqrsConfigurationLog.Category) ?? NullLogger.Instance);

    /// <summary>
    ///     Publishes a notification from <paramref name="services" />. While the configured <see cref="OutboxMode" />
    ///     routes to the outbox, a notification the registered <see cref="INotificationSerializer" /> names goes to the
    ///     outbox for deferred, durable delivery; any other notification, and every notification while the outbox is off,
    ///     is delivered in-process (see <see cref="PublishInProcess{TNotification}" />). Inside a running request of the
    ///     scope, or an outbox delivery (which owns what its handler publishes the same way), it is buffered and stored when
    ///     that succeeds (with its unit of work's commit when it runs in one); anywhere else nothing would settle a buffer,
    ///     so it is written straight to the <see cref="IOutboxStore" />.
    /// </summary>
    /// <param name="services">The publishing scope.</param>
    /// <param name="notification">The notification; not null.</param>
    /// <param name="cancellationToken">A token to cancel the publish.</param>
    /// <returns>A task that completes when the notification has been delivered in-process or stored.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when the notification goes straight to the store but no <see cref="IOutboxStore" /> is registered.
    /// </exception>
    public Task Publish<TNotification>(IServiceProvider services, TNotification notification, CancellationToken cancellationToken)
        where TNotification : INotification
        => _outboxMode == OutboxMode.Disabled
            ? PublishInProcess(services, notification, cancellationToken)
            : PublishUnderOutboxMode(services, notification, cancellationToken);

    /// <summary>Publishes a notification in-process, bypassing the outbox, resolving its handlers and behaviors from <paramref name="services" />.</summary>
    /// <param name="services">The publishing scope.</param>
    /// <param name="notification">The notification; not null.</param>
    /// <param name="cancellationToken">A token to cancel the publish.</param>
    /// <returns>A task that completes when every handler has.</returns>
    public Task PublishInProcess<TNotification>(IServiceProvider services, TNotification notification, CancellationToken cancellationToken)
        where TNotification : INotification
    {
        // A value type's runtime type is its static type; asking the instance would box it.
        var runtimeType = typeof(TNotification).IsValueType ? typeof(TNotification) : notification.GetType();
        if (runtimeType == typeof(TNotification))
        {
            var plan = Plan<TNotification>();
            return PublishAs(services, notification, runtimeType, plan, plan.Subscriptions, cancellationToken);
        }

        return _routes.TryGetValue(runtimeType, out var route)
            ? route.Publish(this, services, notification, cancellationToken)
            : PublishAs(services, notification, runtimeType, Plan<TNotification>(), SubscriptionsOf(runtimeType), cancellationToken);
    }

    /// <summary>
    ///     Delivers a notification to one subscription (the outbox's per-handler delivery) in <paramref name="services" />,
    ///     wrapped by the notification pipeline behaviors of its runtime type.
    /// </summary>
    /// <param name="services">The delivering scope.</param>
    /// <param name="subscription">The subscription the delivery is addressed to.</param>
    /// <param name="notification">The notification; assignable to the subscription's declared type.</param>
    /// <param name="cancellationToken">A token to cancel the delivery.</param>
    /// <returns>The delivery's task.</returns>
    public Task Deliver(IServiceProvider services, NotificationSubscription subscription, INotification notification, CancellationToken cancellationToken)
        => _routes.TryGetValue(notification.GetType(), out var route)
            ? route.Deliver(this, services, subscription, notification, cancellationToken)
            : subscription.DeliverAsDeclared(this, services, notification, cancellationToken);

    /// <summary>
    ///     Whether a publish of <typeparamref name="TNotification" /> may reach anything: a subscription, a handler
    ///     registered by hand, or a behavior. <c>false</c> only when the provider proves none exists, so publishing it can
    ///     be skipped.
    /// </summary>
    public bool MayReachAnyone<TNotification>() where TNotification : INotification
    {
        var plan = Plan<TNotification>();
        return plan.Subscriptions.Count > 0 || plan.MayHaveHandRegisteredHandlers || plan.MayHaveBehaviors;
    }

    /// <summary>
    ///     The types of the handlers registered by hand for a routed notification type, which an outbox delivery never
    ///     reaches: the outbox delivers to subscriptions only. Resolving them constructs them, which may throw.
    /// </summary>
    public Type[] HandRegisteredHandlerTypes(Type notificationType, IServiceProvider services)
        => _routes.TryGetValue(notificationType, out var route) ? route.HandRegisteredHandlerTypes(this, services) : [];

    public void Dispose() => _plans.Dispose();

    /// <summary>Publishes a notification whose runtime type is <typeparamref name="TNotification" /> as that type.</summary>
    internal Task PublishAsRuntimeType<TNotification>(IServiceProvider services, TNotification notification, CancellationToken cancellationToken)
        where TNotification : INotification
    {
        var plan = Plan<TNotification>();
        return PublishAs(services, notification, typeof(TNotification), plan, plan.Subscriptions, cancellationToken);
    }

    /// <summary>Delivers to one subscription through the behaviors of <typeparamref name="TNotification" />.</summary>
    internal Task DeliverAs<TNotification>(
        IServiceProvider services,
        NotificationSubscription subscription,
        TNotification notification,
        CancellationToken cancellationToken)
        where TNotification : INotification
    {
        var plan = Plan<TNotification>();
        if (plan.MayHaveBehaviors && NotificationBehaviors.Resolve(services, plan) is { Length: > 0 } behaviors)
            return NotificationBehaviors.Run(behaviors, notification,
                ct => NotificationBehaviors.Start(subscription, services, notification, ct), cancellationToken);

        return NotificationBehaviors.Start(subscription, services, notification, cancellationToken);
    }

    /// <summary>The handler types <see cref="HandRegisteredHandlerTypes(Type, IServiceProvider)" /> reports for <typeparamref name="TNotification" />.</summary>
    internal Type[] HandRegisteredHandlerTypes<TNotification>(IServiceProvider services) where TNotification : INotification
    {
        // Not the full plan: this runs at host start for every routed type, and building a plan also resolves what
        // wraps the type, which is not what is being reported on here.
        if (!_registrations.MayBeRegistered(typeof(INotificationHandler<TNotification>))) return [];
        return Array.ConvertAll(
            HandRegisteredHandlers<TNotification>(services, SubscriptionsOf(typeof(TNotification))),
            handler => handler.GetType());
    }

    private Task PublishUnderOutboxMode<TNotification>(IServiceProvider services, TNotification notification, CancellationToken cancellationToken)
        where TNotification : INotification
    {
        // The unit of work is only consulted in Transactional mode.
        if (_outboxMode == OutboxMode.Transactional)
        {
            if (services.GetService<IUnitOfWork>() is not { } unitOfWork)
                return PublishWithoutUnitOfWork(services, notification, cancellationToken);
            if (!unitOfWork.HasActiveTransaction)
                return PublishInProcess(services, notification, cancellationToken);
        }

        // The serializer that will store the notification is the one that decides whether it can be stored: a name from
        // it is the opt-in to the outbox, which keeps framework notifications (and anything it cannot serialize)
        // in-process. Asked of the scope on every publish, as it would be to store the notification.
        var serializer = services.GetService<INotificationSerializer>();
        if (serializer is null) return PublishInProcess(services, notification, cancellationToken);
        if (!serializer.TryGetNotificationName(notification.GetType(), out var name))
            return PublishUnnamed(services, notification, serializer, cancellationToken);

        CheckDurableHandlers(services, notification, name);

        // Buffered only inside a running request of this scope, which then settles it. A publish from anywhere else (a
        // controller, a hosted service, a stream's consumer between items) has no request here to settle it, so it goes
        // straight to the store rather than into a buffer nothing would ever store.
        if (services.GetService<ScopedOutbox>() is { } outbox && outbox.TryBuffer(notification))
            return Task.CompletedTask;

        return RequestOutboxScope.StoreAsync(services, [notification], cancellationToken);
    }

    // Transactional mode with no unit of work registered: there is never a transaction to store a notification in. A
    // notification the serializer names was meant to be durable, so its publish fails with CQRCONF007 rather than
    // quietly delivering it in-process; any other is delivered in-process, as it would be with a unit of work.
    private Task PublishWithoutUnitOfWork<TNotification>(IServiceProvider services, TNotification notification, CancellationToken cancellationToken)
        where TNotification : INotification
    {
        if (services.GetService<INotificationSerializer>() is { } serializer)
            return serializer.TryGetNotificationName(notification.GetType(), out _)
                ? Task.FromException(new InvalidOperationException(
                    CqrsConfigurationRules.FailureMessage(CqrsConfigurationRules.TransactionalOutboxWithoutUnitOfWork)))
                : PublishUnnamed(services, notification, serializer, cancellationToken);

        return PublishInProcess(services, notification, cancellationToken);
    }

    // A notification the serializer does not name, published while an outbox mode is on, is delivered in-process. The
    // type's naming check (CQRCONF003 / CQRCONF010) runs once per provider, found through the plan of the notification's
    // runtime type; a type that lost its name to another module's fails the publish instead.
    private Task PublishUnnamed<TNotification>(
        IServiceProvider services,
        TNotification notification,
        INotificationSerializer serializer,
        CancellationToken cancellationToken)
        where TNotification : INotification
    {
        // A value type's runtime type is its static type; asking the instance would box it.
        var runtimeType = typeof(TNotification).IsValueType ? typeof(TNotification) : notification.GetType();
        var failure = runtimeType == typeof(TNotification)
            ? OutboxNamingFailure<TNotification>(serializer)
            : _routes.TryGetValue(runtimeType, out var route) ? route.OutboxNamingFailure(this, serializer) : null;

        return failure is null
            ? PublishInProcess(services, notification, cancellationToken)
            : Task.FromException(new InvalidOperationException(failure));
    }

    // A notification that goes to the outbox: the handlers registered by hand for its runtime type, which the outbox never
    // reaches, are checked once per provider (CQRCONF011 / CQRCONF012).
    private void CheckDurableHandlers<TNotification>(IServiceProvider services, TNotification notification, string name)
        where TNotification : INotification
    {
        var runtimeType = typeof(TNotification).IsValueType ? typeof(TNotification) : notification.GetType();
        if (runtimeType == typeof(TNotification))
            CheckDurableHandlers<TNotification>(services, name);
        else if (_routes.TryGetValue(runtimeType, out var route))
            route.CheckDurableHandlers(this, services, name);
    }

    /// <summary>
    ///     Runs the check of the handlers registered by hand for <typeparamref name="TNotification" />, a routed type, for a
    ///     publish that goes to the outbox (see <see cref="DurableHandlersCheck{TNotification}" />).
    /// </summary>
    internal void CheckDurableHandlers<TNotification>(IServiceProvider services, string name) where TNotification : INotification
        => Plan<TNotification>().DurableHandlers?.Check(this, services, name, _configurationLogger);

    /// <summary>
    ///     The configuration error of <typeparamref name="TNotification" /> for a publish the serializer declined to name, or
    ///     <see langword="null" /> (see <see cref="OutboxNamingCheck" />).
    /// </summary>
    internal string? OutboxNamingFailure<TNotification>(INotificationSerializer serializer) where TNotification : INotification
        => Plan<TNotification>().OutboxNaming?.Failure(serializer, _outboxMode, _configurationLogger);

    private NotificationPlan<TNotification> Plan<TNotification>() where TNotification : INotification
        => _plans.Get(this, static (owner, self) => self.BuildPlan<TNotification>(owner));

    private NotificationPlan<TNotification> BuildPlan<TNotification>(ProviderPlanCache owner) where TNotification : INotification
    {
        var behaviors = _registrations.Behaviors<TNotification>(typeof(INotificationPipelineBehavior<TNotification>));
        var subscriptions = SubscriptionsOf(typeof(TNotification));
        // The generator never registers a notification handler under its interface, so for most types the container proves
        // there are none and a publish skips resolving them.
        var mayHaveHandRegisteredHandlers = _registrations.MayBeRegistered(typeof(INotificationHandler<TNotification>));
        // The configuration checks are asked of the routed types only, as the startup validator asks them.
        var routedUnderOutbox = _outboxMode != OutboxMode.Disabled && _routes.ContainsKey(typeof(TNotification));
        return new NotificationPlan<TNotification>
        {
            Owner = owner,
            Subscriptions = subscriptions,
            MayHaveHandRegisteredHandlers = mayHaveHandRegisteredHandlers,
            MayHaveBehaviors = behaviors.MayHaveAny,
            MergesDiscoveredBehaviors = behaviors.MergesDiscovered,
            UsesClosedBehaviors = behaviors.UsesClosedSet,
            OutboxNaming = routedUnderOutbox && subscriptions.Count > 0 ? new OutboxNamingCheck(typeof(TNotification)) : null,
            DurableHandlers = routedUnderOutbox && mayHaveHandRegisteredHandlers ? new DurableHandlersCheck<TNotification>() : null
        };
    }

    private IReadOnlyList<NotificationSubscription> SubscriptionsOf(Type runtimeType)
        => _subscriptions?.GetSubscriptions(runtimeType) ?? Array.Empty<NotificationSubscription>();

    private Task PublishAs<TNotification>(
        IServiceProvider services,
        TNotification notification,
        Type runtimeType,
        NotificationPlan<TNotification> plan,
        IReadOnlyList<NotificationSubscription> subscriptions,
        CancellationToken cancellationToken)
        where TNotification : INotification
    {
        if (_metrics is { NotificationsPublished.Enabled: true })
            _metrics.RecordNotificationPublished(runtimeType);

        // The overwhelmingly common case - no notification behaviors - goes straight to the handlers.
        if (plan.MayHaveBehaviors && NotificationBehaviors.Resolve(services, plan) is { Length: > 0 } behaviors)
            return DeliverThroughBehaviors(behaviors, services, notification, plan, subscriptions, cancellationToken);

        return DeliverToAll(services, notification, plan, subscriptions, cancellationToken);
    }

    // The terminal step's closure captures the notification. Built in a method of its own, it is only allocated when a
    // chain runs: a lambda over a parameter of PublishAs would be allocated on every publish, behaviors or not.
    private Task DeliverThroughBehaviors<TNotification>(
        INotificationPipelineBehavior<TNotification>[] behaviors,
        IServiceProvider services,
        TNotification notification,
        NotificationPlan<TNotification> plan,
        IReadOnlyList<NotificationSubscription> subscriptions,
        CancellationToken cancellationToken)
        where TNotification : INotification
        => NotificationBehaviors.Run(behaviors, notification, ct => DeliverToAll(services, notification, plan, subscriptions, ct), cancellationToken);

    private Task DeliverToAll<TNotification>(
        IServiceProvider services,
        TNotification notification,
        NotificationPlan<TNotification> plan,
        IReadOnlyList<NotificationSubscription> subscriptions,
        CancellationToken cancellationToken)
        where TNotification : INotification
    {
        var byHand = plan.MayHaveHandRegisteredHandlers ? HandRegisteredHandlers<TNotification>(services, subscriptions) : [];

        return (subscriptions.Count + byHand.Length) switch
        {
            0 => Task.CompletedTask,
            1 => subscriptions.Count == 1
                ? NotificationBehaviors.Start(subscriptions[0], services, notification, cancellationToken)
                : NotificationBehaviors.Start(byHand[0], notification, cancellationToken),
            _ => DeliverToManyAsync(services, subscriptions, byHand, notification, cancellationToken)
        };
    }

    /// <summary>
    ///     The handlers registered by hand as <c>INotificationHandler&lt;TNotification&gt;</c> in <paramref name="services" />.
    ///     The generator registers its handlers only by their concrete type, so these are the application's own
    ///     registrations; a registration of a type the generator also discovered (an assembly scan, say) is left out, since
    ///     that handler is already delivered through its subscription.
    /// </summary>
    private static INotificationHandler<TNotification>[] HandRegisteredHandlers<TNotification>(
        IServiceProvider services,
        IReadOnlyList<NotificationSubscription> subscriptions)
        where TNotification : INotification
    {
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

    // Subscriptions first, in the registry's order (by handler name), then the handlers registered by hand, in
    // registration order.
    private async Task DeliverToManyAsync<TNotification>(
        IServiceProvider services,
        IReadOnlyList<NotificationSubscription> subscriptions,
        INotificationHandler<TNotification>[] byHand,
        TNotification notification,
        CancellationToken cancellationToken)
        where TNotification : INotification
    {
        var strategy = _publishStrategy;
        if (strategy == PublishStrategy.Sequential)
        {
            // One at a time, stopping at the first failure.
            for (var i = 0; i < subscriptions.Count; i++)
                await subscriptions[i].Handle(services, notification, cancellationToken).ConfigureAwait(false);
            foreach (var handler in byHand)
                await (handler.Handle(notification, cancellationToken) ?? Task.CompletedTask).ConfigureAwait(false);
            return;
        }

        // Checked before any handler starts: the options validation rejects an undefined strategy at host start, but a
        // provider used without a host never runs it.
        if (strategy is not (PublishStrategy.Parallel or PublishStrategy.ParallelWhenAllAggregate))
            throw new InvalidOperationException($"'{strategy}' is not a defined {nameof(PublishStrategy)}.");

        var tasks = new Task[subscriptions.Count + byHand.Length];
        for (var i = 0; i < subscriptions.Count; i++)
            tasks[i] = NotificationBehaviors.Start(subscriptions[i], services, notification, cancellationToken);
        for (var i = 0; i < byHand.Length; i++)
            tasks[subscriptions.Count + i] = NotificationBehaviors.Start(byHand[i], notification, cancellationToken);

        var whenAll = Task.WhenAll(tasks);
        if (strategy == PublishStrategy.Parallel)
        {
            // The first failure surfaces (await's default); the siblings are observed through the WhenAll task.
            await whenAll.ConfigureAwait(false);
            return;
        }

        try
        {
            await whenAll.ConfigureAwait(false);
        }
        catch
        {
            // ParallelWhenAllAggregate: every failure surfaces, since await rethrows only the first. A single failure is
            // rethrown as itself, not wrapped.
            var failures = whenAll.Exception?.InnerExceptions;
            if (failures is { Count: > 1 })
                ExceptionDispatchInfo.Capture(new AggregateException(failures)).Throw();
            throw;
        }
    }
}
