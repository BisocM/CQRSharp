using System.Collections.Concurrent;
using CQRSharp.Abstractions.Attributes.Pipelines;
using CQRSharp.Abstractions.Interfaces.Context;
using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Interfaces.Markers.Query;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Abstractions.Models.Requests;
using CQRSharp.Core.Caching.Contexts;
using CQRSharp.Core.Caching.Handlers;
using CQRSharp.Core.Caching.Requests;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Notifications.Pipelines;
using CQRSharp.Core.Notifications.Types;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Pipelines;

/// <summary>
///     Everything the executor needs to run one request type, resolved once per service provider instead of on every
///     dispatch: the registry lookups (metadata, handler type, invoker, context factory), the sorted interceptors, and —
///     the part that makes the common case cheap — which optional stages this provider can skip entirely because
///     nothing is registered for them (pipeline behaviors, lifecycle-notification subscribers).
/// </summary>
internal sealed class RequestPlan<TRequest, TResult> where TRequest : IRequest
{
    public required RequestPlanCache Owner { get; init; }
    public required RequestMetadata Metadata { get; init; }
    public required Type HandlerType { get; init; }

    /// <summary>The generator's typed invoker: returns the handler's own task — no boxing, no extra state machine.</summary>
    public Func<object, TRequest, CancellationToken, Task<TResult>>? TypedInvoker { get; init; }

    /// <summary>The object-returning invoker, used when a module was built by a generator without typed invokers.</summary>
    public HandlerInvokerDelegate? LegacyInvoker { get; init; }

    public required Func<IServiceProvider, object?>? ContextFactoryResolver { get; init; }
    public required Type ContextType { get; init; }

    /// <summary>The context factory is the built-in default, so the context can be constructed without touching DI.</summary>
    public required bool UsesDefaultContextFactory { get; init; }

    /// <summary><c>false</c> only when the provider can prove no pipeline behavior is registered for this pair.</summary>
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

/// <summary>
///     Singleton cache of <see cref="RequestPlan{TRequest,TResult}" /> per request type for one service provider.
/// </summary>
internal sealed class RequestPlanCache(
    IServiceProvider rootProvider,
    IRequestRegistry requestRegistry,
    IHandlerRegistry handlerRegistry,
    IContextFactoryRegistry contextFactoryRegistry)
{
    private readonly ConcurrentDictionary<Type, object> _plans = new();

    // Null when the container does not expose registration queries (a non-Microsoft container, a hand-built test
    // provider). Every "can this stage be skipped?" question then answers "no", which is the pre-5.0 behavior.
    private readonly IServiceProviderIsService? _isService = rootProvider.GetService<IServiceProviderIsService>();

    public RequestPlan<TRequest, TResult> Get<TRequest, TResult>() where TRequest : IRequest
    {
        // One static slot per closed generic remembers the last provider's plan, so the steady state is a reference
        // comparison rather than a dictionary lookup. A process with several providers falls back to the dictionary.
        var cached = Slot<TRequest, TResult>.Plan;
        if (cached is not null && ReferenceEquals(cached.Owner, this)) return cached;

        var plan = (RequestPlan<TRequest, TResult>)_plans.GetOrAdd(
            typeof(TRequest),
            static (_, self) => self.Build<TRequest, TResult>(),
            this);

        Slot<TRequest, TResult>.Plan = plan;
        return plan;
    }

    private RequestPlan<TRequest, TResult> Build<TRequest, TResult>() where TRequest : IRequest
    {
        var requestType = typeof(TRequest);

        if (!requestRegistry.TryGetRequestMetadata(requestType, out var metadata) || metadata is null)
            throw new InvalidOperationException($"No metadata for request '{requestType.Name}'.");

        var handlerType = requestRegistry.TryGetHandlerType(requestType)
                          ?? throw new InvalidOperationException($"Handler for '{requestType.Name}' not found.");

        handlerRegistry.TryGetHandlerDelegate(requestType, out var legacyInvoker);
        var typedInvoker = handlerRegistry.TryGetTypedInvoker(requestType) as Func<object, TRequest, CancellationToken, Task<TResult>>;

        var contextType = metadata.ContextType ?? typeof(RequestContextBase);

        return new RequestPlan<TRequest, TResult>
        {
            Owner = this,
            Metadata = metadata,
            HandlerType = handlerType,
            TypedInvoker = typedInvoker,
            LegacyInvoker = legacyInvoker,
            ContextType = contextType,
            // A registry that does not hand out its resolver (a custom or mocked one) is asked per request instead.
            ContextFactoryResolver = contextFactoryRegistry.TryGetResolver(contextType)
                                     ?? (services => contextFactoryRegistry.TryGetFactory(contextType, services)),
            UsesDefaultContextFactory = UsesDefaultContextFactory(contextType),
            MayHaveBehaviors = IsRegistered(typeof(IPipelineBehavior<TRequest, TResult>)),
            MayHaveLifecycleSubscribers = MayHaveLifecycleSubscribers<TRequest, TResult>(),
            PreHandlers = Sorted(metadata.PreHandlers, PreHandlerComparer.Instance),
            PostHandlers = Sorted(metadata.PostHandlers, PostHandlerComparer.Instance)
        };
    }

    private bool IsRegistered(Type serviceType) => _isService is null || _isService.IsService(serviceType);

    private bool HasSubscribers<TNotification>() where TNotification : INotification
        => IsRegistered(typeof(INotificationHandler<TNotification>)) ||
           IsRegistered(typeof(INotificationPipelineBehavior<TNotification>));

    // Skipping a lifecycle publish is only sound when the publish would have gone through the built-in dispatchers: a
    // replaced INotificationDispatcher (a decorator, a test double) observes every publish regardless of registrations.
    private bool? _publishesThroughBuiltInDispatcher;

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
        if (_isService is null || !PublishesThroughBuiltInDispatcher()) return true;

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

    // The default context is "new RequestContextBase()" behind a transient factory. When that built-in factory is what
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

    private static class Slot<TRequest, TResult> where TRequest : IRequest
    {
        public static volatile RequestPlan<TRequest, TResult>? Plan;
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
