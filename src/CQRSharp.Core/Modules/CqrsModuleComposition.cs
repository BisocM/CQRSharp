using System.Collections.Frozen;
using System.ComponentModel;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Exceptions;
using CQRSharp.Core.Idempotency;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Pipelines;
using CQRSharp.Core.Registries;
using CQRSharp.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CQRSharp.Core.Modules;

/// <summary>
///     Wires the per-assembly <see cref="ICqrsModule" /> registrations (the composition root's own module plus every
///     referenced assembly's) into the single set of framework services CQRSharp resolves at runtime: the registries and
///     route tables, merged once per service provider. Generated bootstrap code calls this once, after every module has
///     been registered.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class CqrsModuleComposition
{
    /// <summary>
    ///     Registers the merged registries and route table built from every registered
    ///     <see cref="ICqrsModule" />. Authoritative (RemoveAll-then-register) so it wins regardless of ordering and is
    ///     idempotent on re-invocation.
    /// </summary>
    /// <param name="services">The service collection that the modules were registered into.</param>
    /// <returns>The same service collection, to allow chaining.</returns>
    public static IServiceCollection AddCqrsModuleComposition(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Where two modules describe the same request, the last registered wins, exactly as for its route below: the
        // bootstrap registers referenced modules first and the composition root's own last, so the application's handler
        // for a request wins over a library's, and its metadata and invoker come from the same module as its route.
        services.RemoveAll<IRequestRegistry>();
        services.AddSingleton<IRequestRegistry>(sp =>
            new RequestRegistry(LastWins(sp.GetServices<ICqrsModule>(), m => m.RequestMetadata)));

        services.RemoveAll<IHandlerRegistry>();
        services.AddSingleton<IHandlerRegistry>(sp =>
            new HandlerRegistry(LastWins(sp.GetServices<ICqrsModule>(), m => m.HandlerInvokers)));

        services.RemoveAll<IContextFactoryRegistry>();
        services.AddSingleton<IContextFactoryRegistry>(sp => new ContextFactoryRegistry(MergeContextSources(sp.GetServices<ICqrsModule>())));

        // The built-in factory of the default context. Registered here, after every module registrar, so that a factory
        // for RequestContextBase a module discovered or one registered by hand before this call takes precedence; one
        // registered by hand afterwards, or discovered by a module a later AddCqrsGenerated registers, replaces it.
        DiscoveredContextFactories.RegisterDefault(services);

        services.RemoveAll<IRequestExceptionHookRegistry>();
        services.AddSingleton<IRequestExceptionHookRegistry>(sp =>
            new RequestExceptionHookRegistry(MergeExceptionHooks(sp.GetServices<ICqrsModule>())));

        // The behaviors of a request with a value-type result and of a value-type notification, closed by the modules'
        // generated factories: Microsoft's container cannot close an open-generic registration over a value type where
        // dynamic code is unavailable.
        ClosedPipelineBehaviors.Register(services);

        // The routing is a singleton table built once from the modules: creating a DI scope, which every HTTP request
        // does, must not rebuild a dictionary of every request type in the application.
        services.RemoveAll<ModuleRouteTable>();
        services.AddSingleton(sp => new ModuleRouteTable(sp.GetServices<ICqrsModule>()));

        // Which handlers a notification reaches, in-process and through the outbox alike.
        services.RemoveAll<INotificationSubscriptionRegistry>();
        services.AddSingleton<INotificationSubscriptionRegistry>(sp => new NotificationSubscriptionRegistry(sp.GetServices<ICqrsModule>()));

        // The idempotency behavior's payload fingerprints: every module's generated fingerprinter, first match wins.
        services.RemoveAll<IRequestFingerprinter>();
        services.AddSingleton<IRequestFingerprinter>(sp =>
            new CompositeRequestFingerprinter(sp.GetServices<ICqrsModule>()));

        services.RemoveAll<ICqrsDiagnostics>();
        services.AddScoped<ICqrsDiagnostics>(sp => new CqrsDiagnostics(
            sp.GetRequiredService<ModuleRouteTable>(),
            sp,
            sp.GetRequiredService<IRequestRegistry>(),
            sp.GetRequiredService<IContextFactoryRegistry>()));

        // The outbox serializer, over every module's generated one. TryAdd: a serializer registered with
        // AddNotificationSerializer<T>() before this ran replaces it (one registered afterwards removes this one).
        services.TryAddSingleton<INotificationSerializer>(sp =>
            new CompositeOutboxNotificationSerializer(sp.GetServices<ICqrsModule>()));

        return services;
    }

    private static FrozenDictionary<Type, RequestContextSource> MergeContextSources(IEnumerable<ICqrsModule> modules)
    {
        // Every module's source for a context type is the same object, so which one is kept does not matter. The default
        // context always has one: a request that implements the request interfaces directly is created with it.
        var sources = new Dictionary<Type, RequestContextSource>();
        foreach (var module in modules)
        foreach (var source in module.ContextSources)
            sources[source.Key] = source.Value;

        sources.TryAdd(typeof(RequestContextBase), RequestContextSource.For<RequestContextBase>());
        return sources.ToFrozenDictionary();
    }

    private static FrozenDictionary<Type, RequestExceptionHookInvoker> MergeExceptionHooks(IEnumerable<ICqrsModule> modules)
    {
        var pairs = new Dictionary<Type, Dictionary<Type, RequestExceptionHook>>();
        foreach (var module in modules)
        foreach (var hook in module.ExceptionHooks)
        {
            if (!pairs.TryGetValue(hook.RequestType, out var byException))
                pairs[hook.RequestType] = byException = new Dictionary<Type, RequestExceptionHook>();

            // Merged role by role. A module's hook for a pair carries only the roles that module declares (a library may
            // declare the action and the application the handler), and each role's invoker resolves every module's
            // actions or handlers for the pair from the container: the first invoker of each role covers them all, and
            // keeping one of each runs every hook once.
            byException[hook.ExceptionType] = byException.TryGetValue(hook.ExceptionType, out var existing)
                ? new RequestExceptionHook(existing.RequestType, existing.ExceptionType, existing.InheritanceDepth,
                    existing.Actions ?? hook.Actions, existing.Handlers ?? hook.Handlers)
                : hook;
        }

        var merged = new Dictionary<Type, RequestExceptionHookInvoker>(pairs.Count);
        foreach (var request in pairs)
        {
            var ordered = request.Value.Values
                .OrderByDescending(h => h.InheritanceDepth)
                .ThenBy(h => h.ExceptionType.FullName, StringComparer.Ordinal)
                .ToArray();
            merged[request.Key] = Compose(ordered);
        }

        return merged.ToFrozenDictionary();

        // Every matching action runs first (they are side effects of the exception, whatever becomes of it), then the
        // handlers from the most derived exception type up; the first that handles decides the outcome.
        static RequestExceptionHookInvoker Compose(RequestExceptionHook[] hooks)
            => async (services, request, exception, cancellationToken) =>
            {
                foreach (var hook in hooks)
                    if (hook.Actions is { } actions && hook.ExceptionType.IsInstanceOfType(exception))
                        await actions(services, request, exception, cancellationToken).ConfigureAwait(false);

                foreach (var hook in hooks)
                {
                    if (hook.Handlers is not { } handlers || !hook.ExceptionType.IsInstanceOfType(exception)) continue;
                    var outcome = await handlers(services, request, exception, cancellationToken).ConfigureAwait(false);
                    if (outcome.Handled) return outcome;
                }

                return RequestExceptionHandlingOutcome.NotHandled;
            };
    }

    private static FrozenDictionary<Type, TValue> LastWins<TValue>(
        IEnumerable<ICqrsModule> modules,
        Func<ICqrsModule, IReadOnlyDictionary<Type, TValue>> selector)
    {
        var merged = new Dictionary<Type, TValue>();
        foreach (var module in modules)
        foreach (var entry in selector(module))
            merged[entry.Key] = entry.Value;

        return merged.ToFrozenDictionary();
    }
}
