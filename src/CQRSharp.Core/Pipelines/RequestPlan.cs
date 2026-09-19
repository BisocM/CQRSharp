using System.Collections.Concurrent;
using CQRSharp.Core.Caching.Contexts;
using CQRSharp.Core.Caching.Handlers;
using CQRSharp.Core.Caching.Requests;
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
    public required Type HandlerType { get; init; }

    public required Func<IServiceProvider, object?> ContextFactoryResolver { get; init; }
    public required Type ContextType { get; init; }

    /// <summary>The context factory is the built-in default, so the context can be constructed without touching DI.</summary>
    public required bool UsesDefaultContextFactory { get; init; }

    /// <summary><c>false</c> only when the provider can prove no pipeline behavior is registered for this request.</summary>
    public required bool MayHaveBehaviors { get; init; }

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
    IContextFactoryRegistry contextFactoryRegistry)
{
    private readonly ConcurrentDictionary<Type, RequestPlanBase> _plans = new();

    // Null when the container does not expose registration queries (a non-Microsoft container, a hand-built test
    // provider). Every "can this stage be skipped?" question then answers "no", which is the pre-5.0 behavior.
    private readonly IServiceProviderIsService? _isService = rootProvider.GetService<IServiceProviderIsService>();

    // Skipping a lifecycle publish is only sound when the publish would have gone through the built-in dispatchers: a
    // replaced INotificationDispatcher (a decorator, a test double) observes every publish regardless of registrations.
    private bool? _publishesThroughBuiltInDispatcher;

    public RequestPlan<TRequest, TResult> Get<TRequest, TResult>() where TRequest : IRequest
    {
        // One static slot per closed generic remembers the last provider's plan, so the steady state is a reference
        // comparison rather than a dictionary lookup. A process with several providers falls back to the dictionary.
        var cached = Slot<RequestPlan<TRequest, TResult>>.Plan;
        if (cached is not null && ReferenceEquals(cached.Owner, this)) return cached;

        var plan = (RequestPlan<TRequest, TResult>)_plans.GetOrAdd(
            typeof(TRequest),
            static (_, self) => self.Build<TRequest, TResult>(),
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
            static (_, self) => self.BuildStream<TRequest, TItem>(),
            this);

        Slot<StreamPlan<TRequest, TItem>>.Plan = plan;
        return plan;
    }

    private RequestPlan<TRequest, TResult> Build<TRequest, TResult>() where TRequest : IRequest
    {
        var (metadata, handlerType, contextType) = Resolve(typeof(TRequest));
        return new RequestPlan<TRequest, TResult>
        {
            Owner = this,
            Metadata = metadata,
            HandlerType = handlerType,
            Invoker = ResolveInvoker<Func<object, TRequest, CancellationToken, Task<TResult>>>(typeof(TRequest)),
            ContextType = contextType,
            ContextFactoryResolver = ResolveContextFactory(contextType),
            UsesDefaultContextFactory = UsesDefaultContextFactory(contextType),
            MayHaveBehaviors = IsRegistered(typeof(IPipelineBehavior<TRequest, TResult>)),
            MayHaveLifecycleSubscribers = MayHaveLifecycleSubscribers<TRequest, TResult>(),
            PreHandlers = Sorted(metadata.PreHandlers, PreHandlerComparer.Instance),
            PostHandlers = Sorted(metadata.PostHandlers, PostHandlerComparer.Instance)
        };
    }

    private StreamPlan<TRequest, TItem> BuildStream<TRequest, TItem>() where TRequest : IRequest
    {
        var (metadata, handlerType, contextType) = Resolve(typeof(TRequest));
        return new StreamPlan<TRequest, TItem>
        {
            Owner = this,
            Metadata = metadata,
            HandlerType = handlerType,
            Invoker = ResolveInvoker<Func<object, TRequest, CancellationToken, IAsyncEnumerable<TItem>>>(typeof(TRequest)),
            ContextType = contextType,
            ContextFactoryResolver = ResolveContextFactory(contextType),
            UsesDefaultContextFactory = UsesDefaultContextFactory(contextType),
            MayHaveBehaviors = IsRegistered(typeof(IStreamPipelineBehavior<TRequest, TItem>)),
            MayHaveLifecycleSubscribers = !CanProveNoSubscribers() ||
                                          HasSubscribers<StreamInitiatedNotification<TItem>>() ||
                                          HasSubscribers<StreamCompletedNotification<TItem>>() ||
                                          HasSubscribers<StreamFailedNotification<TItem>>(),
            PreHandlers = Sorted(metadata.PreHandlers, PreHandlerComparer.Instance),
            PostHandlers = Sorted(metadata.PostHandlers, PostHandlerComparer.Instance)
        };
    }

    private (RequestMetadata Metadata, Type HandlerType, Type ContextType) Resolve(Type requestType)
    {
        if (!requestRegistry.TryGetRequestMetadata(requestType, out var metadata) || metadata is null)
            throw new InvalidOperationException($"No metadata for request '{requestType.Name}'.");

        var handlerType = requestRegistry.TryGetHandlerType(requestType)
                          ?? throw new InvalidOperationException($"Handler for '{requestType.Name}' not found.");

        return (metadata, handlerType, metadata.ContextType ?? typeof(RequestContextBase));
    }

    private TInvoker ResolveInvoker<TInvoker>(Type requestType) where TInvoker : Delegate
        => handlerRegistry.TryGetInvoker(requestType) as TInvoker
           ?? throw new InvalidOperationException($"No handler delegate found for request '{requestType.Name}'.");

    // A registry that does not hand out its resolver (a custom or mocked one) is asked per request instead.
    private Func<IServiceProvider, object?> ResolveContextFactory(Type contextType)
        => contextFactoryRegistry.TryGetResolver(contextType)
           ?? (services => contextFactoryRegistry.TryGetFactory(contextType, services));

    private bool IsRegistered(Type serviceType) => _isService is null || _isService.IsService(serviceType);

    private bool HasSubscribers<TNotification>() where TNotification : INotification
        => IsRegistered(typeof(INotificationHandler<TNotification>)) ||
           IsRegistered(typeof(INotificationPipelineBehavior<TNotification>));

    private bool CanProveNoSubscribers() => _isService is not null && PublishesThroughBuiltInDispatcher();

    private bool PublishesThroughBuiltInDispatcher()
    {
        if (_publishesThroughBuiltInDispatcher is { } known) return known;

        bool builtIn;
        try
        {
            using var scope = rootProvider.CreateScope();
            builtIn = scope.ServiceProvider.GetService<Notifications.INotificationDispatcher>()
                is Notifications.NotificationDispatcher { DispatchesThroughBuiltInPipeline: true };
        }
        catch
        {
            builtIn = false;
        }

        _publishesThroughBuiltInDispatcher = builtIn;
        return builtIn;
    }

    private bool MayHaveLifecycleSubscribers<TRequest, TResult>()
    {
        if (!CanProveNoSubscribers()) return true;

        if (typeof(ICommand).IsAssignableFrom(typeof(TRequest)))
            return HasSubscribers<CommandInitiatedNotification>() ||
                   HasSubscribers<CommandCompletedNotification>() ||
                   HasSubscribers<CommandFailedNotification>();

        if (typeof(IQuery<TResult>).IsAssignableFrom(typeof(TRequest)))
            return HasSubscribers<QueryInitiatedNotification<TResult>>() ||
                   HasSubscribers<QueryCompletedNotification<TResult>>() ||
                   HasSubscribers<QueryFailedNotification<TResult>>();

        // Neither (a value-returning command routed through the query path): no lifecycle notification is published.
        return false;
    }

    // The default context is "new RequestContextBase(now)" behind a transient factory. When that built-in factory is what
    // the provider resolves, constructing the context directly is identical and skips a DI resolution per request.
    private bool UsesDefaultContextFactory(Type contextType)
    {
        if (contextType != typeof(RequestContextBase) || _isService is null) return false;

        try
        {
            using var scope = rootProvider.CreateScope();
            var factory = contextFactoryRegistry.TryGetFactory(contextType, scope.ServiceProvider);
            return factory is not null && factory.GetType() == typeof(DefaultRequestContextFactory);
        }
        catch
        {
            return false;
        }
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
    }

    private static int CompareByPriorityThenName(int leftPriority, int rightPriority, object left, object right)
    {
        var byPriority = leftPriority.CompareTo(rightPriority);
        return byPriority != 0
            ? byPriority
            : string.CompareOrdinal(left.GetType().FullName, right.GetType().FullName);
    }

    private sealed class PreHandlerComparer : IComparer<IPreHandlerAttribute>
    {
        public static readonly PreHandlerComparer Instance = new();

        public int Compare(IPreHandlerAttribute? x, IPreHandlerAttribute? y)
            => ReferenceEquals(x, y) ? 0 : x is null ? -1 : y is null ? 1
                : CompareByPriorityThenName(x.PreHandlerExecutionPriority, y.PreHandlerExecutionPriority, x, y);
    }

    private sealed class PostHandlerComparer : IComparer<IPostHandlerAttribute>
    {
        public static readonly PostHandlerComparer Instance = new();

        public int Compare(IPostHandlerAttribute? x, IPostHandlerAttribute? y)
            => ReferenceEquals(x, y) ? 0 : x is null ? -1 : y is null ? 1
                : CompareByPriorityThenName(x.PostHandlerExecutionPriority, y.PostHandlerExecutionPriority, x, y);
    }
}
