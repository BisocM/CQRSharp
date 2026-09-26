using System.ComponentModel;
using CQRSharp.Core.Exceptions;
using CQRSharp.Core.Idempotency;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Pipelines;
using CQRSharp.Core.Registries;
using CQRSharp.Persistence;

namespace CQRSharp.Core.Modules;

/// <summary>
///     What one assembly's source-generated module contributes to the application: the requests, handlers, context
///     types, exception hooks and notifications the generator discovered in that assembly at compile time, as tables
///     keyed by type. Every value is data, a static lambda over the assembly's types, or an object the generated code
///     creates by closing one of CQRSharp.Core's generic factories over them (<see cref="RequestRoute" />,
///     <see cref="NotificationRoute" />, …), so the behavior behind every entry lives in CQRSharp.Core and only the list of
///     types is compiled into the assembly. A composition root registers its own module and every referenced assembly's,
///     and <see cref="CqrsModuleComposition.AddCqrsModuleComposition" /> merges them into provider-wide tables, the
///     composition root's module winning where two modules route the same request.
/// </summary>
/// <remarks>
///     Implemented only by the source generator; a hand-written module is not supported. Members added after 5.0.0 come
///     with default implementations, so a module compiled against an earlier 5.x keeps loading.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface ICqrsModule
{
    /// <summary>The metadata (handler, context type, interceptors, exemptions) of every request this assembly handles.</summary>
    IReadOnlyDictionary<Type, RequestMetadata> RequestMetadata { get; }

    /// <summary>
    ///     The typed handler invoker of every request this assembly handles: a
    ///     <c>Func&lt;object, TRequest, CancellationToken, Task&lt;TResult&gt;&gt;</c> for a command or query, a
    ///     <c>Func&lt;object, TRequest, CancellationToken, IAsyncEnumerable&lt;TItem&gt;&gt;</c> for a streaming request. Each
    ///     returns the handler's own task or stream.
    /// </summary>
    IReadOnlyDictionary<Type, Delegate> HandlerInvokers { get; }

    /// <summary>The source of the contexts of every context type this assembly's requests use or its factories create.</summary>
    IReadOnlyDictionary<Type, RequestContextSource> ContextSources { get; }

    /// <summary>The route of every command and query this assembly handles, keyed by exact request type.</summary>
    IReadOnlyDictionary<Type, RequestRoute> RequestRoutes { get; }

    /// <summary>The route of every streaming request this assembly handles, keyed by exact request type.</summary>
    IReadOnlyDictionary<Type, StreamRoute> StreamRoutes { get; }

    /// <summary>The exception hooks this assembly declares, one entry per (request, exception type) pair.</summary>
    IReadOnlyList<RequestExceptionHook> ExceptionHooks { get; }

    /// <summary>The route of every concrete notification type this assembly declares or handles.</summary>
    IReadOnlyDictionary<Type, NotificationRoute> NotificationRoutes { get; }

    /// <summary>
    ///     This assembly's notification subscriptions: one per (handler type, handled notification type) pair. The
    ///     composition merges every module's into the <see cref="INotificationSubscriptionRegistry" /> that decides which
    ///     handlers a notification reaches, in-process and through the outbox.
    /// </summary>
    IReadOnlyList<NotificationSubscription> NotificationSubscriptions { get; }

    /// <summary>
    ///     The partition key selectors of this assembly's <c>[NotificationName(PartitionBy = ...)]</c> notifications, keyed
    ///     by notification type.
    /// </summary>
    IReadOnlyDictionary<Type, Func<INotification, string?>> PartitionKeySelectors { get; }

    /// <summary>
    ///     This assembly's generated outbox serializer, which names and serializes its <c>[NotificationName]</c>
    ///     notifications, or <see langword="null" /> when it has none. The composition puts every module's behind the
    ///     application's one <see cref="INotificationSerializer" />, unless a custom serializer replaces it.
    /// </summary>
    INotificationSerializer? OutboxSerializer { get; }

    /// <summary>
    ///     This assembly's payload fingerprinter for the <see cref="IIdempotentRequest" /> types it declares or handles, or
    ///     <see langword="null" /> when none of them can be fingerprinted automatically.
    /// </summary>
    IRequestFingerprinter? RequestFingerprinter { get; }
}
