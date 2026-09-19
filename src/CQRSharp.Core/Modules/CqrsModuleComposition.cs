using System.Collections.Concurrent;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Interfaces.Markers.Stream;
using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Abstractions.Models.Requests;
using CQRSharp.Core.Caching.Contexts;
using CQRSharp.Core.Caching.Handlers;
using CQRSharp.Core.Caching.Requests;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Exceptions;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Options;
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

        // Dispatchers: scoped composites that route by runtime type to the owning module's generated dispatcher.
        services.RemoveAll<IRequestDispatcher>();
        services.AddScoped<IRequestDispatcher>(sp =>
            new CompositeRequestDispatcher(sp.GetServices<ICqrsModule>(), sp.GetRequiredService<IPipelineExecutor>()));

        services.RemoveAll<IStreamRequestDispatcher>();
        services.AddScoped<IStreamRequestDispatcher>(sp =>
            new CompositeStreamRequestDispatcher(sp.GetServices<ICqrsModule>(), sp.GetRequiredService<IPipelineExecutor>()));

        services.RemoveAll<IDirectNotificationDispatcher>();
        services.AddScoped<IDirectNotificationDispatcher>(sp =>
            new CompositeDirectNotificationDispatcher(sp.GetServices<ICqrsModule>(), sp));

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
///     Routes a request to the owning module's source-generated dispatcher by runtime type. Built once per scope from
///     every registered module; the per-module dispatchers carry the AOT-safe typed switch.
/// </summary>
internal sealed class CompositeRequestDispatcher : IRequestDispatcher
{
    private readonly Dictionary<Type, IRequestDispatcher> _byRequestType = new();

    public CompositeRequestDispatcher(IEnumerable<ICqrsModule> modules, IPipelineExecutor pipelineExecutor)
    {
        foreach (var module in modules)
        {
            if (module.RequestTypes.Count == 0) continue;
            var dispatcher = module.CreateRequestDispatcher(pipelineExecutor);
            foreach (var requestType in module.RequestTypes)
                _byRequestType[requestType] = dispatcher;
        }
    }

    public Task<TResponse> ExecuteAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Resolve(request.GetType()).ExecuteAsync(request, cancellationToken);
    }

    public Task<object?> ExecuteAsync(IRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Resolve(request.GetType()).ExecuteAsync(request, cancellationToken);
    }

    private IRequestDispatcher Resolve(Type requestType)
        => _byRequestType.TryGetValue(requestType, out var dispatcher)
            ? dispatcher
            : throw new InvalidOperationException(
                $"No handler or pipeline found for request type '{requestType.FullName}'. Ensure it's public or internal, has a corresponding handler, and its assembly's CQRSharp module is registered.");
}

/// <summary>
///     Routes a streaming request to the owning module's source-generated stream dispatcher by runtime type.
/// </summary>
internal sealed class CompositeStreamRequestDispatcher : IStreamRequestDispatcher
{
    private readonly Dictionary<Type, IStreamRequestDispatcher> _byRequestType = new();

    public CompositeStreamRequestDispatcher(IEnumerable<ICqrsModule> modules, IPipelineExecutor pipelineExecutor)
    {
        foreach (var module in modules)
        {
            if (module.StreamRequestTypes.Count == 0) continue;
            var dispatcher = module.CreateStreamDispatcher(pipelineExecutor);
            foreach (var requestType in module.StreamRequestTypes)
                _byRequestType[requestType] = dispatcher;
        }
    }

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
        => _byRequestType.TryGetValue(requestType, out var dispatcher)
            ? dispatcher
            : throw new InvalidOperationException(
                $"No stream handler or pipeline found for request type '{requestType.FullName}'. Ensure it's public or internal, has a corresponding handler, and its assembly's CQRSharp module is registered.");
}

/// <summary>
///     Routes an untyped notification to the owning module's source-generated dispatcher by runtime type. The typed
///     <c>Publish&lt;T&gt;</c> path is inherited from <see cref="DirectNotificationDispatcher" /> and already spans
///     assemblies via DI handler resolution, so only the runtime-typed bridge needs per-module routing.
/// </summary>
internal sealed class CompositeDirectNotificationDispatcher : DirectNotificationDispatcher
{
    private readonly Dictionary<Type, IDirectNotificationDispatcher> _byNotificationType = new();

    public CompositeDirectNotificationDispatcher(IEnumerable<ICqrsModule> modules, IServiceProvider services)
        : base(services)
    {
        foreach (var module in modules)
        {
            if (module.HandledNotificationTypes.Count == 0) continue;
            var dispatcher = module.CreateNotificationDispatcher(services);
            foreach (var notificationType in module.HandledNotificationTypes)
                _byNotificationType[notificationType] = dispatcher;
        }
    }

    public override Task Publish(INotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return _byNotificationType.TryGetValue(notification.GetType(), out var dispatcher)
            ? dispatcher.Publish(notification, cancellationToken)
            : Task.CompletedTask;
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
