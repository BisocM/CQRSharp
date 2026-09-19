using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Abstractions.Models.Requests;
using CQRSharp.Core.Caching.Contexts;
using CQRSharp.Core.Caching.Handlers;
using CQRSharp.Core.Caching.Requests;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Exceptions;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Pipelines;

namespace CQRSharp.Core.Modules;

/// <summary>
///     The per-assembly registration surface the CQRSharp source generator emits: one implementation per assembly
///     that contains handlers, carrying that assembly's compile-time-discovered registration data and its AOT-safe,
///     reflection-free typed-dispatch entry points. A composition-root assembly registers its own module plus every
///     referenced assembly's module as additive singletons, and <c>AddCqrsModuleComposition</c> merges them into the
///     single set of framework services (one registry, one dispatcher per kind) — which is how CQRSharp spans multiple
///     assemblies without the generated registration code colliding across them.
/// </summary>
public interface ICqrsModule
{
    /// <summary>Per-request metadata (handler type, context, pre/post handlers, exemptions) discovered in this assembly.</summary>
    IReadOnlyDictionary<Type, RequestMetadata> RequestMetadata { get; }

    /// <summary>Per-request compiled handler invokers discovered in this assembly.</summary>
    IReadOnlyDictionary<Type, HandlerInvokerDelegate> HandlerInvokers { get; }

    /// <summary>Per-context-type factory resolvers discovered in this assembly.</summary>
    IReadOnlyDictionary<Type, Func<IServiceProvider, object?>> ContextFactories { get; }

    /// <summary>Per-request exception-hook invokers discovered in this assembly.</summary>
    IReadOnlyDictionary<Type, RequestExceptionHookInvoker> ExceptionHooks { get; }

    /// <summary>The command/query request types this module's request dispatcher can execute.</summary>
    IReadOnlyList<Type> RequestTypes { get; }

    /// <summary>The streaming request types this module's stream dispatcher can execute.</summary>
    IReadOnlyList<Type> StreamRequestTypes { get; }

    /// <summary>Every concrete notification type declared in this assembly (the notification-registry surface).</summary>
    IReadOnlyList<Type> NotificationTypes { get; }

    /// <summary>The notification types this assembly has an in-process handler for (the dispatch-routing surface).</summary>
    IReadOnlyList<Type> HandledNotificationTypes { get; }

    /// <summary>The subset of <see cref="HandledNotificationTypes" /> that carry a stable <c>[NotificationName]</c>.</summary>
    IReadOnlyList<Type> StableNotificationTypes { get; }

    /// <summary>Creates this module's AOT-safe request dispatcher, bound to the supplied scoped pipeline executor.</summary>
    IRequestDispatcher CreateRequestDispatcher(IPipelineExecutor pipelineExecutor);

    /// <summary>Creates this module's AOT-safe stream-request dispatcher, bound to the supplied scoped pipeline executor.</summary>
    IStreamRequestDispatcher CreateStreamDispatcher(IPipelineExecutor pipelineExecutor);

    /// <summary>Creates this module's AOT-safe direct notification dispatcher, bound to the supplied scope.</summary>
    IDirectNotificationDispatcher CreateNotificationDispatcher(IServiceProvider services);

    /// <summary>Creates this module's diagnostics describer over the (already merged) registries.</summary>
    ICqrsDiagnostics CreateDiagnostics(
        IServiceProvider services,
        IRequestRegistry requestRegistry,
        IContextFactoryRegistry contextFactoryRegistry,
        ICqrsNotificationRegistry notificationRegistry);

    /// <summary>This module's outbox notification serializer, or <see langword="null" /> when it has no stable-named notifications.</summary>
    INotificationSerializer? OutboxSerializer { get; }
}
