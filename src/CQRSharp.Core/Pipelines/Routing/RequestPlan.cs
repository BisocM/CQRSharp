using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Registries;
using CQRSharp.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Pipelines;

/// <summary>
///     Everything the executor needs to run one request type, resolved once per service provider instead of on every
///     dispatch: the registry lookups (metadata, handler type, invoker, context factory), the sorted interceptors, and —
///     the part that makes the common case cheap — which optional stages this provider can skip entirely because
///     nothing is registered for them (pipeline behaviors, lifecycle-notification subscribers).
/// </summary>
internal abstract class RequestPlanBase : ProviderPlan
{
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
    ///     Whether the behaviors the request's markers depend on are registered (<c>CQRCONF005</c> / <c>CQRCONF006</c>),
    ///     checked against the behaviors its first dispatch resolves; <see langword="null" /> for a request without markers.
    /// </summary>
    public required MarkerBehaviorCheck? MarkerCheck { get; init; }

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
    IContextFactoryRegistry contextFactoryRegistry,
    NotificationPublisher notifications,
    ILoggerFactory loggerFactory) : IDisposable
{
    private readonly ProviderPlanCache _plans = new();
    private readonly ILogger _configurationLogger = loggerFactory.CreateLogger(CqrsConfigurationLog.Category);
    private readonly ProviderRegistrations _registrations = new(rootProvider);
    private bool? _usesDefaultContextFactory;

    public RequestPlan<TRequest, TResult> Get<TRequest, TResult>() where TRequest : IRequest
        => _plans.Get(this, static (owner, self) => self.Build<TRequest, TResult>(owner));

    public StreamPlan<TRequest, TItem> GetStream<TRequest, TItem>() where TRequest : IRequest
        => _plans.Get(this, static (owner, self) => self.BuildStream<TRequest, TItem>(owner));

    public void Dispose() => _plans.Dispose();

    private RequestPlan<TRequest, TResult> Build<TRequest, TResult>(ProviderPlanCache owner) where TRequest : IRequest
    {
        var metadata = Resolve(typeof(TRequest));
        var behaviors = _registrations.Behaviors<TResult>(typeof(IPipelineBehavior<TRequest, TResult>));
        return new RequestPlan<TRequest, TResult>
        {
            Owner = owner,
            Metadata = metadata,
            Invoker = ResolveInvoker<Func<object, TRequest, CancellationToken, Task<TResult>>>(typeof(TRequest)),
            ContextSource = contextFactoryRegistry.TryGetSource(metadata.ContextType),
            UsesDefaultContextFactory = UsesDefaultContextFactory(metadata.ContextType),
            MayHaveBehaviors = behaviors.MayHaveAny,
            MergesDiscoveredBehaviors = behaviors.MergesDiscovered,
            UsesClosedBehaviors = behaviors.UsesClosedSet,
            MayHaveLifecycleSubscribers = MayHaveLifecycleSubscribers<TRequest, TResult>(),
            PreHandlers = Sorted(metadata.PreHandlers, PreHandlerComparer.Instance),
            PostHandlers = Sorted(metadata.PostHandlers, PostHandlerComparer.Instance),
            MarkerCheck = MarkerBehaviorCheck.For(typeof(TRequest), behaviors.MayHaveAny, _configurationLogger)
        };
    }

    private StreamPlan<TRequest, TItem> BuildStream<TRequest, TItem>(ProviderPlanCache owner) where TRequest : IRequest
    {
        var metadata = Resolve(typeof(TRequest));
        var behaviors = _registrations.Behaviors<TItem>(typeof(IStreamPipelineBehavior<TRequest, TItem>));
        return new StreamPlan<TRequest, TItem>
        {
            Owner = owner,
            Metadata = metadata,
            Invoker = ResolveInvoker<Func<object, TRequest, CancellationToken, IAsyncEnumerable<TItem>>>(typeof(TRequest)),
            ContextSource = contextFactoryRegistry.TryGetSource(metadata.ContextType),
            UsesDefaultContextFactory = UsesDefaultContextFactory(metadata.ContextType),
            MayHaveBehaviors = behaviors.MayHaveAny,
            MergesDiscoveredBehaviors = behaviors.MergesDiscovered,
            UsesClosedBehaviors = behaviors.UsesClosedSet,
            MayHaveLifecycleSubscribers = notifications.MayReachAnyone<StreamInitiatedNotification<TItem>>() ||
                                          notifications.MayReachAnyone<StreamCompletedNotification<TItem>>() ||
                                          notifications.MayReachAnyone<StreamFailedNotification<TItem>>(),
            PreHandlers = Sorted(metadata.PreHandlers, PreHandlerComparer.Instance),
            PostHandlers = Sorted(metadata.PostHandlers, PostHandlerComparer.Instance),
            MarkerCheck = MarkerBehaviorCheck.For(typeof(TRequest), behaviors.MayHaveAny, _configurationLogger)
        };
    }

    private RequestMetadata Resolve(Type requestType)
        => requestRegistry.TryGetRequestMetadata(requestType, out var metadata)
            ? metadata
            : throw new InvalidOperationException($"No metadata for request '{requestType.Name}'.");

    private TInvoker ResolveInvoker<TInvoker>(Type requestType) where TInvoker : Delegate
        => handlerRegistry.TryGetInvoker(requestType) as TInvoker
           ?? throw new InvalidOperationException($"No handler delegate found for request '{requestType.Name}'.");

    // Lifecycle notifications go through the notification publisher, so whether one would reach anyone is its plan's
    // answer: the same rule decides whether it is published and what a publish of it runs.
    private bool MayHaveLifecycleSubscribers<TRequest, TResult>()
        => RequestKindOf<TRequest, TResult>.Value switch
        {
            // Every command publishes the Command* notifications, a value-returning one (ICommand<T>) included.
            RequestKind.Command => notifications.MayReachAnyone<CommandInitiatedNotification>() ||
                                   notifications.MayReachAnyone<CommandCompletedNotification>() ||
                                   notifications.MayReachAnyone<CommandFailedNotification>(),
            RequestKind.Query => notifications.MayReachAnyone<QueryInitiatedNotification<TResult>>() ||
                                 notifications.MayReachAnyone<QueryCompletedNotification<TResult>>() ||
                                 notifications.MayReachAnyone<QueryFailedNotification<TResult>>(),
            _ => false
        };

    // The default context is "new RequestContextBase(now)" behind a transient factory. When that built-in factory is what
    // the provider resolves, constructing the context directly is identical and skips a DI resolution per request.
    private bool UsesDefaultContextFactory(Type contextType)
    {
        if (contextType != typeof(RequestContextBase) || !_registrations.CanProveAbsence) return false;
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
