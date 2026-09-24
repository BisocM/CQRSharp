using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CQRSharp.Core.Modules;
using CQRSharp.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CQRSharp.Core.Pipelines;

/// <summary>
///     A source-generated way to construct one open-generic behavior closed over one request whose result (or streamed
///     item) is a value type, or over one value-type notification. Microsoft's container refuses to close an open-generic
///     registration over a value type under Native AOT, so for such requests and notifications the behaviors are built by
///     these factories instead, and the executor and the notification pipeline resolve that set.
/// </summary>
/// <param name="openBehaviorType">The open behavior type the consumer registered, e.g. <c>typeof(LoggingBehavior&lt;,&gt;)</c>.</param>
/// <param name="serviceType">
///     The closed service type, e.g. <c>typeof(IPipelineBehavior&lt;GetCount, int&gt;)</c> or
///     <c>typeof(INotificationPipelineBehavior&lt;Tick&gt;)</c>.
/// </param>
/// <param name="create">Constructs the closed behavior from the resolving scope.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ClosedBehaviorFactory(Type openBehaviorType, Type serviceType, Func<IServiceProvider, object> create)
{
    /// <summary>The open-generic behavior type this factory closes.</summary>
    public Type OpenBehaviorType { get; } = openBehaviorType ?? throw new ArgumentNullException(nameof(openBehaviorType));

    /// <summary>The closed behavior interface the constructed instance is registered under.</summary>
    public Type ServiceType { get; } = serviceType ?? throw new ArgumentNullException(nameof(serviceType));

    /// <summary>Constructs the closed behavior, resolving its dependencies from the given scope.</summary>
    public Func<IServiceProvider, object> Create { get; } = create ?? throw new ArgumentNullException(nameof(create));
}

/// <summary>One generated module's <see cref="ClosedBehaviorFactory" /> entries, registered by its registrar.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ClosedBehaviorCatalog(IReadOnlyList<ClosedBehaviorFactory> factories)
{
    /// <summary>The factories, one per (open behavior, request or notification) pair the module's generator could close.</summary>
    public IReadOnlyList<ClosedBehaviorFactory> Factories { get; } = factories ?? throw new ArgumentNullException(nameof(factories));
}

/// <summary>
///     Resolves a request's pipeline behaviors the way the executor does: from the open-generic registrations, or -
///     for a request with a value-type result on a runtime without dynamic code - from the closed set built by the
///     modules' generated factories, merged with the closed behaviors the generator discovered (registered under
///     <see cref="DiscoveredServices.Key" />, so one the application also registers by hand runs once). The diagnostics
///     describer uses it so its view matches the dispatch path, and the notification pipeline resolves a value-type
///     notification's behaviors through the same closed set.
/// </summary>
internal static class PipelineBehaviors
{
    /// <summary>The behaviors that wrap <typeparamref name="TRequest" />, in registration order.</summary>
    public static IPipelineBehavior<TRequest, TResult>[] Resolve<TRequest, TResult>(IServiceProvider services)
        where TRequest : IRequest
    {
        ArgumentNullException.ThrowIfNull(services);
        return ResolveAll<IPipelineBehavior<TRequest, TResult>>(
            services, UsesClosedBehaviors<TResult>(services), MayHaveDiscovered<IPipelineBehavior<TRequest, TResult>>(services));
    }

    /// <summary>The stream behaviors that wrap <typeparamref name="TRequest" />, in registration order.</summary>
    public static IStreamPipelineBehavior<TRequest, TItem>[] ResolveStream<TRequest, TItem>(IServiceProvider services)
        where TRequest : IStreamRequest<TItem>
    {
        ArgumentNullException.ThrowIfNull(services);
        return ResolveAll<IStreamPipelineBehavior<TRequest, TItem>>(
            services, UsesClosedBehaviors<TItem>(services), MayHaveDiscovered<IStreamPipelineBehavior<TRequest, TItem>>(services));
    }

    /// <summary>
    ///     Every behavior of one closed behavior interface, in an array the caller owns (it may reorder or compact it):
    ///     the registered ones from the closed set when <paramref name="fromClosedSet" />, otherwise from the container,
    ///     and, when <paramref name="mergeDiscovered" />, the discovered ones merged in (see <see cref="DiscoveredServices.Merge{TService}" />).
    /// </summary>
    internal static TBehavior[] ResolveAll<TBehavior>(IServiceProvider services, bool fromClosedSet, bool mergeDiscovered)
        where TBehavior : class
    {
        TBehavior[] registered;
        if (fromClosedSet)
        {
            registered = services.GetRequiredService<ClosedBehaviorSet>().Resolve<TBehavior>(services);
        }
        else
        {
            // Microsoft DI hands out a fresh array only when some registration is transient; with every behavior a
            // singleton (or scoped) it returns its own cached array, which a sort or compaction would corrupt for every
            // later dispatch. A single behavior is never reordered or compacted in place.
            var resolved = services.GetServices<TBehavior>();
            registered = resolved as TBehavior[] ?? resolved.ToArray();
            if (registered.Length > 1 && !mergeDiscovered) registered = (TBehavior[])registered.Clone();
        }

        if (!mergeDiscovered) return registered;

        var merged = DiscoveredServices.Merge(services.GetKeyedServices<TBehavior>(DiscoveredServices.Key), registered);
        return merged is TBehavior[] array ? (TBehavior[])array.Clone() : merged.ToArray();
    }

    internal static bool UsesClosedBehaviors<T>(IServiceProvider services)
        => typeof(T).IsValueType && (services.GetService<ClosedBehaviorResolution>()?.Enabled ?? false);

    /// <summary>
    ///     Whether the generator may have registered closed behaviors of <typeparamref name="TBehavior" /> under
    ///     <see cref="DiscoveredServices.Key" />; a container that cannot say is asked for them.
    /// </summary>
    internal static bool MayHaveDiscovered<TBehavior>(IServiceProvider services) => MayHaveDiscovered(services, typeof(TBehavior));

    /// <inheritdoc cref="MayHaveDiscovered{TBehavior}(IServiceProvider)" />
    internal static bool MayHaveDiscovered(IServiceProvider services, Type behaviorService)
        => services.GetService<IServiceProviderIsKeyedService>() is { } isKeyed
            ? isKeyed.IsKeyedService(behaviorService, DiscoveredServices.Key)
            : services is IKeyedServiceProvider;
}

/// <summary>
///     Whether value-type-result requests and value-type notifications resolve their behaviors from the closed set. On by
///     default only where the container's open-generic closing is unavailable (Native AOT); a test registers its own
///     instance to exercise the closed path on any runtime.
/// </summary>
internal sealed class ClosedBehaviorResolution
{
    public static bool Default => !RuntimeFeature.IsDynamicCodeSupported;

    public required bool Enabled { get; init; }
}

/// <summary>
///     Wires the closed behavior set at composition time. The set itself is computed on first use from the final
///     service collection, so a behavior registered after <c>AddCqrsGenerated</c> (the usual MediatR-style order)
///     counts as much as one registered before it.
/// </summary>
internal static class ClosedPipelineBehaviors
{
    public static void Register(IServiceCollection services)
    {
        var resolution = services.FirstOrDefault(d => d.ServiceType == typeof(ClosedBehaviorResolution))?.ImplementationInstance as ClosedBehaviorResolution;
        if (resolution is null)
        {
            resolution = new ClosedBehaviorResolution { Enabled = ClosedBehaviorResolution.Default };
            services.AddSingleton(resolution);
        }

        if (!resolution.Enabled) return;

        // TryAdd: idempotent across a library's own AddCqrsGenerated plus its host's.
        services.TryAddSingleton(root => new ClosedBehaviorSet(services, root));
        services.TryAddScoped<ClosedBehaviorScope>();
        services.TryAddTransient<ClosedBehaviorLease>();
    }
}

/// <summary>
///     The behaviors of value-type-result requests and value-type notifications, built the way the container would build
///     them had it been able to close the open-generic registrations: every registration of the closed behavior
///     interface, in registration order, each open-generic one constructed through the generated factory for that request
///     or notification, each honouring its registration's lifetime. Read from the service collection on first use per
///     closed interface, when it is final. An open-generic registration the generated code recorded as a
///     <see cref="ClosedBehaviorGap" /> for the target is refused: it applies, but nothing can construct it here, so
///     leaving it out would run the request, or deliver the notification, without it.
/// </summary>
internal sealed class ClosedBehaviorSet(IServiceCollection services, IServiceProvider root)
{
    // One array per closed service type, and within it one activation per registration. GetOrAdd always hands out the
    // array it stored, so an activation's identity is stable: the lifetimes' instances are cached on it.
    private readonly ConcurrentDictionary<Type, ClosedBehaviorActivation[]> _byService = new();
    private readonly object _snapshotGate = new();
    private ServiceDescriptor[]? _snapshot;
    private ClosedBehaviorFactory[]? _factories;
    private ClosedBehaviorGap[]? _gaps;

    /// <summary>Whether anything wraps the requests (or notifications) of this closed behavior interface.</summary>
    public bool Has(Type serviceType) => _byService.GetOrAdd(serviceType, Build).Length > 0;

    /// <summary>Constructs the behaviors for one closed behavior interface, in registration order.</summary>
    public TService[] Resolve<TService>(IServiceProvider scope) where TService : class
    {
        var activations = _byService.GetOrAdd(typeof(TService), Build);
        if (activations.Length == 0) return Array.Empty<TService>();

        var behaviors = new TService[activations.Length];
        for (var i = 0; i < activations.Length; i++)
            behaviors[i] = (TService)activations[i].Activate(root, scope);
        return behaviors;
    }

    private ClosedBehaviorActivation[] Build(Type serviceType)
    {
        var (descriptors, factories, gaps) = Snapshot();
        var open = serviceType.IsConstructedGenericType ? serviceType.GetGenericTypeDefinition() : null;
        var activations = new List<ClosedBehaviorActivation>();

        foreach (var descriptor in descriptors)
        {
            if (descriptor.IsKeyedService) continue;

            if (descriptor.ServiceType == serviceType)
            {
                activations.Add(new ClosedBehaviorActivation(descriptor, CreateClosed(descriptor)));
            }
            else if (open is not null && descriptor.ServiceType == open && descriptor.ImplementationType is { IsGenericTypeDefinition: true } implementation)
            {
                if (FindFactory(factories, implementation, serviceType) is { } factory)
                {
                    activations.Add(new ClosedBehaviorActivation(descriptor, factory.Create));
                    continue;
                }

                // No factory: either the behavior's constraints exclude the target, or it applies and generated code
                // could not close it, which the gap records.
                foreach (var gap in gaps)
                    if (gap.Matches(serviceType, implementation))
                        throw gap.ToException();
            }
        }

        return activations.ToArray();
    }

    private static ClosedBehaviorFactory? FindFactory(ClosedBehaviorFactory[] factories, Type openBehaviorType, Type serviceType)
    {
        foreach (var factory in factories)
            if (factory.OpenBehaviorType == openBehaviorType && factory.ServiceType == serviceType)
                return factory;
        return null;
    }

    private static Func<IServiceProvider, object> CreateClosed(ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationFactory is { } factory) return factory;
        var type = descriptor.ImplementationType!;
        return provider => ActivatorUtilities.CreateInstance(provider, type);
    }

    private (ServiceDescriptor[] Descriptors, ClosedBehaviorFactory[] Factories, ClosedBehaviorGap[] Gaps) Snapshot()
    {
        lock (_snapshotGate)
        {
            if (_snapshot is null)
            {
                _snapshot = services.ToArray();
                _factories = Instances<ClosedBehaviorCatalog>(_snapshot).SelectMany(c => c.Factories).ToArray();
                _gaps = Instances<ClosedBehaviorGapCatalog>(_snapshot).SelectMany(c => c.Gaps).ToArray();
            }

            return (_snapshot, _factories!, _gaps!);
        }
    }

    // The catalogs are registered as instances by generated code; nothing else constructs them.
    private static IEnumerable<T> Instances<T>(ServiceDescriptor[] descriptors) where T : class
        => descriptors
            .Where(d => !d.IsKeyedService && d.ServiceType == typeof(T))
            .Select(d => d.ImplementationInstance as T)
            .OfType<T>();
}

/// <summary>
///     One registration serving one closed behavior interface. An open-generic registration is a single descriptor for
///     every closed type it serves, so the instances its lifetime keeps are cached per activation (descriptor and
///     closed type), exactly as the container caches a closed-over open generic per closed type.
/// </summary>
internal sealed class ClosedBehaviorActivation(ServiceDescriptor descriptor, Func<IServiceProvider, object> create)
{
    private readonly object _singletonGate = new();
    private object? _singleton;

    public object Activate(IServiceProvider root, IServiceProvider scope)
    {
        if (descriptor.ImplementationInstance is { } instance) return instance;

        return descriptor.Lifetime switch
        {
            // From the root: a singleton must not capture the first dispatching scope's services.
            ServiceLifetime.Singleton => Singleton(root),
            ServiceLifetime.Scoped => scope.GetRequiredService<ClosedBehaviorScope>().GetOrAdd(this, scope),
            _ => Build(scope)
        };
    }

    /// <summary>
    ///     Constructs the behavior in <paramref name="owner" />, the provider whose lifetime it shares, and hands a
    ///     disposable one to that provider to dispose. The lease is resolved only after the behavior's dependencies were,
    ///     so the provider, which disposes in reverse order of creation, disposes the behavior before them.
    /// </summary>
    public object Build(IServiceProvider owner)
    {
        var instance = create(owner);
        if (instance is IDisposable or IAsyncDisposable)
            owner.GetRequiredService<ClosedBehaviorLease>().Hold(instance);
        return instance;
    }

    // Not a Lazy: a constructor that throws is retried on the next dispatch, as the container retries a singleton.
    private object Singleton(IServiceProvider root)
    {
        var built = Volatile.Read(ref _singleton);
        if (built is not null) return built;

        lock (_singletonGate)
        {
            built = _singleton;
            if (built is null)
            {
                built = Build(root);
                Volatile.Write(ref _singleton, built);
            }

            return built;
        }
    }
}

/// <summary>The scoped closed behaviors of one DI scope, one instance per activation.</summary>
internal sealed class ClosedBehaviorScope
{
    private readonly Dictionary<ClosedBehaviorActivation, object> _instances = new(ReferenceEqualityComparer.Instance);

    public object GetOrAdd(ClosedBehaviorActivation activation, IServiceProvider scope)
    {
        lock (_instances)
        {
            if (!_instances.TryGetValue(activation, out var instance))
                _instances[activation] = instance = activation.Build(scope);
            return instance;
        }
    }
}

/// <summary>
///     Hands one disposable closed behavior to the provider that resolved the lease, which disposes it with everything
///     else it created, in the container's order. Disposing it synchronously fails for a behavior that can only be
///     disposed asynchronously, as the container fails for a service of its own.
/// </summary>
internal sealed class ClosedBehaviorLease : IDisposable, IAsyncDisposable
{
    private object? _instance;

    public void Hold(object instance) => _instance = instance;

    public void Dispose()
    {
        switch (Interlocked.Exchange(ref _instance, null))
        {
            case IDisposable disposable:
                disposable.Dispose();
                break;
            case IAsyncDisposable asyncOnly:
                throw new InvalidOperationException(
                    $"'{asyncOnly.GetType()}' type only implements IAsyncDisposable. Use DisposeAsync to dispose the container.");
        }
    }

    public ValueTask DisposeAsync()
    {
        switch (Interlocked.Exchange(ref _instance, null))
        {
            case IAsyncDisposable asyncDisposable:
                return asyncDisposable.DisposeAsync();
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }

        return default;
    }
}
