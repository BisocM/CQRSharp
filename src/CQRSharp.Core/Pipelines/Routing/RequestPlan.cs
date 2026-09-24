using System.Collections.Concurrent;
using CQRSharp.Core.Registries;
using CQRSharp.Pipelines;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Pipelines;

/// <summary>
///     Everything the executor needs to run one request type, resolved once per service provider instead of on every
///     dispatch: the registry lookups (metadata, handler type, invoker, context factory), the sorted interceptors, and —
///     the part that makes the common case cheap — which optional stages this provider can skip entirely because
///     nothing is registered for them (pipeline behaviors, lifecycle-notification subscribers).
/// </summary>
internal abstract class RequestPlanBase
{
    public required RequestPlanCache Owner { get; init; }
    public required RequestMetadata Metadata { get; init; }
    public Type HandlerType => Metadata.HandlerType;

    /// <summary>Where the request's context comes from; <c>null</c> when no module maps its context type.</summary>
    public required RequestContextSource? ContextSource { get; init; }
    public Type ContextType => Metadata.ContextType;

    /// <summary>The context factory is the built-in default, so the context can be constructed without touching DI.</summary>
    public required bool UsesDefaultContextFactory { get; init; }

    /// <summary><c>false</c> only when the provider can prove no pipeline behavior is registered for this request.</summary>
    public required bool MayHaveBehaviors { get; init; }

    /// <summary>
    ///     Closed behaviors the generator discovered may be registered for this request under the discovered-services key,
    ///     so they are merged with the application's own registrations.
    /// </summary>
    public required bool MergesDiscoveredBehaviors { get; init; }

    /// <summary>
    ///     The behaviors come from the closed, keyed set the module composition registered (a value-type result on a
    ///     runtime without dynamic code) rather than from the open-generic registrations.
    /// </summary>
    public required bool UsesClosedBehaviors { get; init; }

    /// <summary><c>false</c> only when the provider can prove nothing subscribes to this request's lifecycle notifications.</summary>
    public required bool MayHaveLifecycleSubscribers { get; init; }

    public required IPreHandlerAttribute[] PreHandlers { get; init; }
    public required IPostHandlerAttribute[] PostHandlers { get; init; }

    /// <summary>
    ///     Nothing brackets the handler: no interceptors and no lifecycle subscribers. The final action is then just the
    ///     handler call, with no notifications to publish on either outcome and so no state machine to wrap it in.
    /// </summary>
    public bool IsBareHandler => !MayHaveLifecycleSubscribers && PreHandlers.Length == 0 && PostHandlers.Length == 0;
}

/// <summary>The plan for a command or query.</summary>
internal sealed class RequestPlan<TRequest, TResult> : RequestPlanBase where TRequest : IRequest
{
    /// <summary>The generator's typed invoker: returns the handler's own task — no boxing, no extra state machine.</summary>
    public required Func<object, TRequest, CancellationToken, Task<TResult>> Invoker { get; init; }
}

/// <summary>The plan for a streaming request.</summary>
internal sealed class StreamPlan<TRequest, TItem> : RequestPlanBase where TRequest : IRequest
{
    /// <summary>The generator's typed invoker: returns the handler's own stream.</summary>
    public required Func<object, TRequest, CancellationToken, IAsyncEnumerable<TItem>> Invoker { get; init; }
}

/// <summary>
///     Singleton cache of request plans per request type for one service provider.
/// </summary>
internal sealed class RequestPlanCache(
    IServiceProvider rootProvider,
    IRequestRegistry requestRegistry,
    IHandlerRegistry handlerRegistry,
    IContextFactoryRegistry contextFactoryRegistry) : IDisposable
{
    private readonly ConcurrentDictionary<Type, RequestPlanBase> _plans = new();

    // One reset per static slot this cache ever filled, so disposing the provider (a test host, an in-process restart)
    // does not leave its plans - and through them the provider's whole graph - rooted by a static for the process life.
    private readonly ConcurrentBag<Action> _slotResets = new();
    private bool? _usesDefaultContextFactory;

    // Null when the container does not expose registration queries (a non-Microsoft container, a hand-built test
    // provider). Every "can this stage be skipped?" question then answers "no": every stage runs, which is always correct.
    private readonly IServiceProviderIsService? _isService = rootProvider.GetService<IServiceProviderIsService>();

    public RequestPlan<TRequest, TResult> Get<TRequest, TResult>() where TRequest : IRequest
    {
        // One static slot per closed generic remembers the last provider's plan, so the steady state is a reference
        // comparison rather than a dictionary lookup. A process with several providers falls back to the dictionary.
        var cached = Slot<RequestPlan<TRequest, TResult>>.Plan;
        if (cached is not null && ReferenceEquals(cached.Owner, this)) return cached;

        var plan = (RequestPlan<TRequest, TResult>)_plans.GetOrAdd(
            typeof(TRequest),
            static (_, self) =>
            {
                var built = self.Build<TRequest, TResult>();
                // Registered once the plan exists: a build that throws (a misconfigured registry) is retried on the next
                // dispatch and must not leave a reset behind per attempt.
                self._slotResets.Add(() => Slot<RequestPlan<TRequest, TResult>>.Forget(self));
                return built;
            },
            this);

        Slot<RequestPlan<TRequest, TResult>>.Plan = plan;
        return plan;
    }

    public StreamPlan<TRequest, TItem> GetStream<TRequest, TItem>() where TRequest : IRequest
    {
        var cached = Slot<StreamPlan<TRequest, TItem>>.Plan;
        if (cached is not null && ReferenceEquals(cached.Owner, this)) return cached;

        var plan = (StreamPlan<TRequest, TItem>)_plans.GetOrAdd(
            typeof(TRequest),
            static (_, self) =>
            {
                var built = self.BuildStream<TRequest, TItem>();
                self._slotResets.Add(() => Slot<StreamPlan<TRequest, TItem>>.Forget(self));
                return built;
            },
            this);

        Slot<StreamPlan<TRequest, TItem>>.Plan = plan;
        return plan;
    }

    public void Dispose()
    {
        foreach (var reset in _slotResets) reset();
        _plans.Clear();
    }

    private RequestPlan<TRequest, TResult> Build<TRequest, TResult>() where TRequest : IRequest
    {
        var metadata = Resolve(typeof(TRequest));
        var discovered = HasDiscoveredBehaviors(typeof(IPipelineBehavior<TRequest, TResult>));
        return new RequestPlan<TRequest, TResult>
        {
            Owner = this,
            Metadata = metadata,
            Invoker = ResolveInvoker<Func<object, TRequest, CancellationToken, Task<TResult>>>(typeof(TRequest)),
            ContextSource = contextFactoryRegistry.TryGetSource(metadata.ContextType),
            UsesDefaultContextFactory = UsesDefaultContextFactory(metadata.ContextType),
            MayHaveBehaviors = discovered || (UsesClosedBehaviors<TResult>()
                ? HasClosedBehaviors(typeof(IPipelineBehavior<TRequest, TResult>))
                : IsRegistered(typeof(IPipelineBehavior<TRequest, TResult>))),
            MergesDiscoveredBehaviors = discovered,
            UsesClosedBehaviors = UsesClosedBehaviors<TResult>(),
            MayHaveLifecycleSubscribers = MayHaveLifecycleSubscribers<TRequest, TResult>(),
            PreHandlers = Sorted(metadata.PreHandlers, PreHandlerComparer.Instance),
            PostHandlers = Sorted(metadata.PostHandlers, PostHandlerComparer.Instance)
        };
    }

    private StreamPlan<TRequest, TItem> BuildStream<TRequest, TItem>() where TRequest : IRequest
    {
        var metadata = Resolve(typeof(TRequest));
        var discovered = HasDiscoveredBehaviors(typeof(IStreamPipelineBehavior<TRequest, TItem>));
        return new StreamPlan<TRequest, TItem>
        {
            Owner = this,
            Metadata = metadata,
            Invoker = ResolveInvoker<Func<object, TRequest, CancellationToken, IAsyncEnumerable<TItem>>>(typeof(TRequest)),
            ContextSource = contextFactoryRegistry.TryGetSource(metadata.ContextType),
            UsesDefaultContextFactory = UsesDefaultContextFactory(metadata.ContextType),
            MayHaveBehaviors = discovered || (UsesClosedBehaviors<TItem>()
                ? HasClosedBehaviors(typeof(IStreamPipelineBehavior<TRequest, TItem>))
                : IsRegistered(typeof(IStreamPipelineBehavior<TRequest, TItem>))),
            MergesDiscoveredBehaviors = discovered,
            UsesClosedBehaviors = UsesClosedBehaviors<TItem>(),
            MayHaveLifecycleSubscribers = !CanProveNoSubscribers() ||
                                          HasSubscribers<StreamInitiatedNotification<TItem>>() ||
                                          HasSubscribers<StreamCompletedNotification<TItem>>() ||
                                          HasSubscribers<StreamFailedNotification<TItem>>(),
            PreHandlers = Sorted(metadata.PreHandlers, PreHandlerComparer.Instance),
            PostHandlers = Sorted(metadata.PostHandlers, PostHandlerComparer.Instance)
        };
    }

    private RequestMetadata Resolve(Type requestType)
        => requestRegistry.TryGetRequestMetadata(requestType, out var metadata)
            ? metadata
            : throw new InvalidOperationException($"No metadata for request '{requestType.Name}'.");

    private TInvoker ResolveInvoker<TInvoker>(Type requestType) where TInvoker : Delegate
        => handlerRegistry.TryGetInvoker(requestType) as TInvoker
           ?? throw new InvalidOperationException($"No handler delegate found for request '{requestType.Name}'.");

    private bool IsRegistered(Type serviceType) => _isService is null || _isService.IsService(serviceType);

    private bool HasDiscoveredBehaviors(Type serviceType) => PipelineBehaviors.MayHaveDiscovered(rootProvider, serviceType);

    private bool HasClosedBehaviors(Type serviceType)
        => rootProvider.GetService<ClosedBehaviorSet>()?.Has(serviceType) ?? false;

    private bool? _closesValueTypeBehaviors;

    private bool UsesClosedBehaviors<TResult>()
        => typeof(TResult).IsValueType &&
           (_closesValueTypeBehaviors ??= rootProvider.GetService<ClosedBehaviorResolution>()?.Enabled ?? false);

    private Notifications.INotificationSubscriptionRegistry? _subscriptions;
    private bool _subscriptionsResolved;

    // A notification has subscribers when a handler registered by hand or a behavior would run for it, or when a
    // generated handler subscribes to it: one declared for the notification's own type, a base type or an interface of
    // it (an INotificationHandler<INotification> audit handler sees every lifecycle notification).
    private bool HasSubscribers<TNotification>() where TNotification : INotification
    {
        if (IsRegistered(typeof(INotificationHandler<TNotification>)) ||
            IsRegistered(typeof(INotificationPipelineBehavior<TNotification>)) ||
            HasDiscoveredBehaviors(typeof(INotificationPipelineBehavior<TNotification>)))
            return true;

        if (!_subscriptionsResolved)
        {
            _subscriptions = rootProvider.GetService<Notifications.INotificationSubscriptionRegistry>();
            _subscriptionsResolved = true;
        }

        return _subscriptions?.GetSubscriptions(typeof(TNotification)).Count > 0;
    }

    // Without registration queries nothing can be proven absent, so every lifecycle notification is published.
    private bool CanProveNoSubscribers() => _isService is not null;

    private bool MayHaveLifecycleSubscribers<TRequest, TResult>()
    {
        if (!CanProveNoSubscribers()) return true;

        return RequestKindOf<TRequest, TResult>.Value switch
        {
            // Every command publishes the Command* notifications, a value-returning one (ICommand<T>) included.
            RequestKind.Command => HasSubscribers<CommandInitiatedNotification>() ||
                                   HasSubscribers<CommandCompletedNotification>() ||
                                   HasSubscribers<CommandFailedNotification>(),
            RequestKind.Query => HasSubscribers<QueryInitiatedNotification<TResult>>() ||
                                 HasSubscribers<QueryCompletedNotification<TResult>>() ||
                                 HasSubscribers<QueryFailedNotification<TResult>>(),
            _ => false
        };
    }

    // The default context is "new RequestContextBase(now)" behind a transient factory. When that built-in factory is what
    // the provider resolves, constructing the context directly is identical and skips a DI resolution per request.
    private bool UsesDefaultContextFactory(Type contextType)
    {
        if (contextType != typeof(RequestContextBase) || _isService is null) return false;
        if (_usesDefaultContextFactory is { } known) return known;

        bool usesDefault;
        try
        {
            // Provider-wide: the answer is the same for every request type, so one scope is opened, not one per plan.
            using var scope = rootProvider.CreateScope();
            usesDefault = contextFactoryRegistry.TryGetSource(contextType)?.ResolveFactory(scope.ServiceProvider) is DefaultRequestContextFactory;
        }
        catch
        {
            usesDefault = false;
        }

        _usesDefaultContextFactory = usesDefault;
        return usesDefault;
    }

    private static T[] Sorted<T>(ReadOnlySpan<T> source, IComparer<T> comparer)
    {
        if (source.Length == 0) return [];
        var sorted = source.ToArray();
        if (sorted.Length > 1) Array.Sort(sorted, comparer);
        return sorted;
    }

    private static class Slot<TPlan> where TPlan : RequestPlanBase
    {
        public static volatile TPlan? Plan;

        public static void Forget(RequestPlanCache owner)
        {
            if (Plan is { } plan && ReferenceEquals(plan.Owner, owner)) Plan = null;
        }
    }

    private sealed class PreHandlerComparer : IComparer<IPreHandlerAttribute>
    {
        public static readonly PreHandlerComparer Instance = new();

        public int Compare(IPreHandlerAttribute? x, IPreHandlerAttribute? y)
            => ReferenceEquals(x, y) ? 0 : x is null ? -1 : y is null ? 1
                : PriorityOrdering.Compare(x.PreHandlerExecutionPriority, y.PreHandlerExecutionPriority, x, y);
    }

    private sealed class PostHandlerComparer : IComparer<IPostHandlerAttribute>
    {
        public static readonly PostHandlerComparer Instance = new();

        public int Compare(IPostHandlerAttribute? x, IPostHandlerAttribute? y)
            => ReferenceEquals(x, y) ? 0 : x is null ? -1 : y is null ? 1
                : PriorityOrdering.Compare(x.PostHandlerExecutionPriority, y.PostHandlerExecutionPriority, x, y);
    }
}
