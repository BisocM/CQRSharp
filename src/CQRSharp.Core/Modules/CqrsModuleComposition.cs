using System.Collections.Concurrent;
using System.Collections.Frozen;
using CQRSharp.Pipelines;
using CQRSharp.Core.Caching.Contexts;
using CQRSharp.Core.Caching.Handlers;
using CQRSharp.Core.Caching.Requests;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Exceptions;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Modules;

/// <summary>
///     Wires the per-assembly <see cref="ICqrsModule" /> registrations (the composition root's own module plus every
///     referenced assembly's) into the single set of framework services CQRSharp resolves at runtime. The registries
///     are merged dictionaries; the dispatchers are composites that route a request, stream, or notification to the
///     owning module's source-generated, AOT-safe dispatcher by runtime type. Generated bootstrap code calls this once,
///     after every module has been registered.
/// </summary>
public static class CqrsModuleComposition
{
    /// <summary>
    ///     Registers the merged registries and routing dispatchers built from every registered <see cref="ICqrsModule" />.
    ///     Authoritative (RemoveAll-then-register) so it wins regardless of ordering and is idempotent on re-invocation.
    /// </summary>
    /// <param name="services">The service collection that the modules were registered into.</param>
    /// <returns>The same service collection, to allow chaining.</returns>
    public static IServiceCollection AddCqrsModuleComposition(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Registries: a single registry per kind, backed by the union of every module's compile-time map.
        services.RemoveAll<IRequestRegistry>();
        services.AddSingleton<IRequestRegistry>(sp =>
            new RequestRegistry(Merge(sp.GetServices<ICqrsModule>(), m => m.RequestMetadata)));

        services.RemoveAll<IHandlerRegistry>();
        services.AddSingleton<IHandlerRegistry>(sp =>
            new HandlerRegistry(Merge(sp.GetServices<ICqrsModule>(), m => m.HandlerInvokers)));

        services.RemoveAll<IContextFactoryRegistry>();
        services.AddSingleton<IContextFactoryRegistry>(sp =>
            new ContextFactoryRegistry(Merge(sp.GetServices<ICqrsModule>(), m => m.ContextFactories)));

        services.RemoveAll<IRequestExceptionHookRegistry>();
        services.AddSingleton<IRequestExceptionHookRegistry>(sp =>
            new RequestExceptionHookRegistry(Merge(sp.GetServices<ICqrsModule>(), m => m.ExceptionHooks)));

        // Dispatchers carry the resolving scope's executor/provider (transient: the scoped ICqrsDispatcher façade keeps
        // the one it resolves), but the routing itself is a singleton table
        // built once from the modules. Creating a DI scope — every HTTP request — must not rebuild a dictionary of every
        // request type in the application.
        services.RemoveAll<ModuleRouteTable>();
        services.AddSingleton(sp => new ModuleRouteTable(sp.GetServices<ICqrsModule>()));

        services.RemoveAll<IRequestDispatcher>();
        services.AddTransient<IRequestDispatcher>(sp =>
        {
            var executor = sp.GetRequiredService<IPipelineExecutor>();
            var table = (executor as PipelineExecutor)?.Shared?.RouteTable ?? sp.GetRequiredService<ModuleRouteTable>();
            return new CompositeRequestDispatcher(table, executor);
        });

        services.RemoveAll<IStreamRequestDispatcher>();
        services.AddTransient<IStreamRequestDispatcher>(sp =>
            new CompositeStreamRequestDispatcher(sp.GetRequiredService<ModuleRouteTable>(), sp.GetRequiredService<IPipelineExecutor>()));

        services.RemoveAll<IDirectNotificationDispatcher>();
        services.AddScoped<IDirectNotificationDispatcher>(sp =>
            new CompositeDirectNotificationDispatcher(sp.GetRequiredService<ModuleRouteTable>(), sp));

        // Notification surface + diagnostics built from the merged modules.
        services.RemoveAll<ICqrsNotificationRegistry>();
        services.AddSingleton<ICqrsNotificationRegistry>(sp =>
            new CompositeCqrsNotificationRegistry(sp.GetServices<ICqrsModule>()));

        services.RemoveAll<ICqrsDiagnostics>();
        services.AddScoped<ICqrsDiagnostics>(sp => new CompositeCqrsDiagnostics(
            sp.GetServices<ICqrsModule>(),
            sp,
            sp.GetRequiredService<IRequestRegistry>(),
            sp.GetRequiredService<IContextFactoryRegistry>(),
            sp.GetRequiredService<ICqrsNotificationRegistry>()));

        // Outbox serializer: TryAdd so a consumer-registered custom serializer still wins. The composite tries each
        // module's serializer in turn (an unrecognised name yields null and falls through to the next).
        services.TryAddSingleton<CompositeOutboxNotificationSerializer>(sp =>
            new CompositeOutboxNotificationSerializer(sp.GetServices<ICqrsModule>()));
        services.TryAddSingleton<INotificationSerializer>(sp => sp.GetRequiredService<CompositeOutboxNotificationSerializer>());
        services.TryAddSingleton<IStableNotificationNameProvider>(sp => sp.GetRequiredService<CompositeOutboxNotificationSerializer>());

        return services;
    }

    private static ConcurrentDictionary<Type, TValue> Merge<TValue>(
        IEnumerable<ICqrsModule> modules,
        Func<ICqrsModule, IReadOnlyDictionary<Type, TValue>> selector)
    {
        var merged = new ConcurrentDictionary<Type, TValue>();
        foreach (var module in modules)
        foreach (var entry in selector(module))
            merged.TryAdd(entry.Key, entry.Value);

        return merged;
    }
}

/// <summary>
///     The application's routing, built once per provider from every registered module: request type to generated route,
///     and — for the surfaces that are still dispatched per module — request/notification type to owning module.
/// </summary>
internal sealed class ModuleRouteTable
{
    public ModuleRouteTable(IEnumerable<ICqrsModule> modules)
    {
        var routes = new Dictionary<Type, RequestRoute>();
        var untypedRoutes = new Dictionary<Type, UntypedRequestRoute>();
        var requestModules = new Dictionary<Type, ICqrsModule>();
        var streamModules = new Dictionary<Type, ICqrsModule>();
        var notificationModules = new Dictionary<Type, ICqrsModule>();

        // Last module wins, matching the pre-5.0 composite dispatchers.
        foreach (var module in modules)
        {
            foreach (var requestType in module.RequestTypes) requestModules[requestType] = module;
            foreach (var route in module.RequestRoutes) routes[route.Key] = route.Value;
            foreach (var route in module.UntypedRequestRoutes) untypedRoutes[route.Key] = route.Value;
            foreach (var streamType in module.StreamRequestTypes) streamModules[streamType] = module;
            foreach (var notificationType in module.HandledNotificationTypes) notificationModules[notificationType] = module;
        }

        // A request whose winning module offers no route must not be served by another module's route.
        foreach (var owner in requestModules)
        {
            if (!owner.Value.RequestRoutes.ContainsKey(owner.Key)) routes.Remove(owner.Key);
            if (!owner.Value.UntypedRequestRoutes.ContainsKey(owner.Key)) untypedRoutes.Remove(owner.Key);
        }

        Routes = routes.ToFrozenDictionary();
        UntypedRoutes = untypedRoutes.ToFrozenDictionary();
        RequestModules = requestModules.ToFrozenDictionary();
        StreamModules = streamModules.ToFrozenDictionary();
        NotificationModules = notificationModules.ToFrozenDictionary();
    }

    public FrozenDictionary<Type, RequestRoute> Routes { get; }
    public FrozenDictionary<Type, UntypedRequestRoute> UntypedRoutes { get; }
    public FrozenDictionary<Type, ICqrsModule> RequestModules { get; }
    public FrozenDictionary<Type, ICqrsModule> StreamModules { get; }
    public FrozenDictionary<Type, ICqrsModule> NotificationModules { get; }
}

/// <summary>
///     Dispatches a request through its source-generated route: one lookup by exact runtime type in the provider-wide
///     <see cref="ModuleRouteTable" />, then a direct call into the executor. Constructing one per scope costs nothing.
/// </summary>
internal sealed class CompositeRequestDispatcher(ModuleRouteTable table, IPipelineExecutor pipelineExecutor) : IRequestDispatcher
{
    /// <summary>The executor this dispatcher routes into (lets the wiring probe recognise the default composition).</summary>
    internal IPipelineExecutor Executor => pipelineExecutor;

    // Only for modules that expose no routes: their own dispatcher, created on first use in this scope.
    private Dictionary<ICqrsModule, IRequestDispatcher>? _moduleDispatchers;

    public Task<TResponse> ExecuteAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return table.Routes.TryGetValue(request.GetType(), out var route)
            ? (Task<TResponse>)route(pipelineExecutor, request, cancellationToken)
            : ResolveModuleDispatcher(request.GetType()).ExecuteAsync(request, cancellationToken);
    }

    public Task<object?> ExecuteAsync(IRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return table.UntypedRoutes.TryGetValue(request.GetType(), out var route)
            ? route(pipelineExecutor, request, cancellationToken)
            : ResolveModuleDispatcher(request.GetType()).ExecuteAsync(request, cancellationToken);
    }

    private IRequestDispatcher ResolveModuleDispatcher(Type requestType)
    {
        if (!table.RequestModules.TryGetValue(requestType, out var module))
            throw new InvalidOperationException(
                $"No handler or pipeline found for request type '{requestType.FullName}'. Ensure it's public or internal, has a corresponding handler, and its assembly's CQRSharp module is registered.");

        _moduleDispatchers ??= new Dictionary<ICqrsModule, IRequestDispatcher>();
        if (!_moduleDispatchers.TryGetValue(module, out var dispatcher))
            _moduleDispatchers[module] = dispatcher = module.CreateRequestDispatcher(pipelineExecutor);

        return dispatcher;
    }
}

/// <summary>
///     Routes a streaming request to the owning module's source-generated stream dispatcher by runtime type. The
///     per-module dispatcher is created on first use in a scope, not eagerly for every module.
/// </summary>
internal sealed class CompositeStreamRequestDispatcher(ModuleRouteTable table, IPipelineExecutor pipelineExecutor) : IStreamRequestDispatcher
{
    private Dictionary<ICqrsModule, IStreamRequestDispatcher>? _moduleDispatchers;

    public IAsyncEnumerable<TItem> ExecuteAsync<TItem>(IStreamRequest<TItem> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Resolve(request.GetType()).ExecuteAsync(request, cancellationToken);
    }

    public IAsyncEnumerable<object?> ExecuteAsync(IStreamRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Resolve(request.GetType()).ExecuteAsync(request, cancellationToken);
    }

    private IStreamRequestDispatcher Resolve(Type requestType)
    {
        if (!table.StreamModules.TryGetValue(requestType, out var module))
            throw new InvalidOperationException(
                $"No stream handler or pipeline found for request type '{requestType.FullName}'. Ensure it's public or internal, has a corresponding handler, and its assembly's CQRSharp module is registered.");

        _moduleDispatchers ??= new Dictionary<ICqrsModule, IStreamRequestDispatcher>();
        if (!_moduleDispatchers.TryGetValue(module, out var dispatcher))
            _moduleDispatchers[module] = dispatcher = module.CreateStreamDispatcher(pipelineExecutor);

        return dispatcher;
    }
}

/// <summary>
///     Routes an untyped notification to the owning module's source-generated dispatcher by runtime type. The typed
///     <c>Publish&lt;T&gt;</c> path is inherited from <see cref="DirectNotificationDispatcher" /> and already spans
///     assemblies via DI handler resolution, so only the runtime-typed bridge needs per-module routing.
/// </summary>
internal sealed class CompositeDirectNotificationDispatcher(ModuleRouteTable table, IServiceProvider services)
    : DirectNotificationDispatcher(services)
{
    private readonly IServiceProvider _scope = services;
    private Dictionary<ICqrsModule, IDirectNotificationDispatcher>? _moduleDispatchers;

    public override Task Publish(INotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!table.NotificationModules.TryGetValue(notification.GetType(), out var module))
            return Task.CompletedTask;

        _moduleDispatchers ??= new Dictionary<ICqrsModule, IDirectNotificationDispatcher>();
        if (!_moduleDispatchers.TryGetValue(module, out var dispatcher))
            _moduleDispatchers[module] = dispatcher = module.CreateNotificationDispatcher(_scope);

        return dispatcher.Publish(notification, cancellationToken);
    }
}

/// <summary>
///     Merges every module's compile-time notification surface (the handled types and which carry a stable outbox name).
/// </summary>
internal sealed class CompositeCqrsNotificationRegistry : ICqrsNotificationRegistry
{
    private readonly Type[] _handled;
    private readonly HashSet<Type> _stable;

    public CompositeCqrsNotificationRegistry(IEnumerable<ICqrsModule> modules)
    {
        var materialized = modules as IReadOnlyCollection<ICqrsModule> ?? modules.ToArray();
        _handled = materialized
            .SelectMany(m => m.NotificationTypes)
            .Distinct()
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToArray();
        _stable = new HashSet<Type>(materialized.SelectMany(m => m.StableNotificationTypes));
    }

    public IReadOnlyList<Type> HandledNotificationTypes => _handled;

    public bool HasStableName(Type notificationType)
    {
        ArgumentNullException.ThrowIfNull(notificationType);
        return _stable.Contains(notificationType);
    }
}

/// <summary>
///     Aggregates each module's source-generated diagnostics: a request is described by the module that owns it, and
///     the configuration inspection runs once over the union of every module's request bindings.
/// </summary>
internal sealed class CompositeCqrsDiagnostics : ICqrsDiagnostics
{
    private readonly ICqrsDiagnostics[] _modules;
    private readonly ICqrsNotificationRegistry _notificationRegistry;
    private readonly IServiceProvider _services;

    public CompositeCqrsDiagnostics(
        IEnumerable<ICqrsModule> modules,
        IServiceProvider services,
        IRequestRegistry requestRegistry,
        IContextFactoryRegistry contextFactoryRegistry,
        ICqrsNotificationRegistry notificationRegistry)
    {
        _services = services;
        _notificationRegistry = notificationRegistry;
        _modules = modules
            .Select(m => m.CreateDiagnostics(services, requestRegistry, contextFactoryRegistry, notificationRegistry))
            .ToArray();
    }

    public bool TryDescribeRequest(Type requestType, out CqrsRequestBinding binding)
    {
        foreach (var module in _modules)
            if (module.TryDescribeRequest(requestType, out binding))
                return true;

        binding = default!;
        return false;
    }

    public CqrsRequestBinding DescribeRequest(Type requestType)
        => TryDescribeRequest(requestType, out var binding)
            ? binding
            : throw new InvalidOperationException(
                $"Unknown request type '{requestType.FullName}'. Ensure it is included in a source-generated module.");

    public IReadOnlyList<CqrsRequestBinding> DescribeAllRequests()
    {
        var list = new List<CqrsRequestBinding>();
        foreach (var module in _modules)
            list.AddRange(module.DescribeAllRequests());
        return list;
    }

    public IReadOnlyList<CqrsBindingIssue> DescribeConfiguration()
    {
        var outbox = _services.GetService<IOptions<OutboxOptions>>()?.Value ?? new OutboxOptions();
        var dispatcher = _services.GetService<IOptions<DispatcherOptions>>()?.Value ?? new DispatcherOptions();
        return CqrsConfigurationInspector.Inspect(
            _services, outbox, dispatcher, DescribeAllRequests(), _notificationRegistry);
    }
}

/// <summary>
///     A single outbox serializer over every module's source-generated serializer. Serialization picks the module that
///     recognises the notification's type; deserialization tries each until one resolves the stable name.
/// </summary>
internal sealed class CompositeOutboxNotificationSerializer : INotificationSerializer, IStableNotificationNameProvider
{
    private readonly INotificationSerializer[] _serializers;

    public CompositeOutboxNotificationSerializer(IEnumerable<ICqrsModule> modules)
        => _serializers = modules
            .Select(m => m.OutboxSerializer)
            .Where(s => s is not null)
            .ToArray()!;

    public byte[] Serialize(INotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        foreach (var serializer in _serializers)
            if (serializer is IStableNotificationNameProvider provider &&
                provider.TryGetStableName(notification.GetType(), out _))
                return serializer.Serialize(notification);

        throw new InvalidOperationException(
            $"Notification type '{notification.GetType().FullName}' is not registered for outbox serialization. Add [NotificationName] or register a custom INotificationSerializer.");
    }

    public INotification? Deserialize(string notificationName, byte[] payload)
    {
        foreach (var serializer in _serializers)
        {
            var result = serializer.Deserialize(notificationName, payload);
            if (result is not null) return result;
        }

        return null;
    }

    public string GetNotificationName(Type notificationType)
    {
        ArgumentNullException.ThrowIfNull(notificationType);
        return TryGetStableName(notificationType, out var name) ? name : notificationType.FullName ?? notificationType.Name;
    }

    public bool TryGetStableName(Type notificationType, out string stableName)
    {
        ArgumentNullException.ThrowIfNull(notificationType);

        foreach (var serializer in _serializers)
            if (serializer is IStableNotificationNameProvider provider &&
                provider.TryGetStableName(notificationType, out stableName))
                return true;

        stableName = string.Empty;
        return false;
    }
}
