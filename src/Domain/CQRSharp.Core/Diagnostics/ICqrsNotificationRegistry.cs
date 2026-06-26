namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     Exposes the notification surface the source generator discovered at compile time: every notification
///     type that has at least one in-process handler, and whether each has a stable outbox name. Used by
///     configuration inspection to flag notifications that would silently dispatch in-process (no stable name)
///     even though an outbox mode is enabled. The only implementer is source-generated and re-emitted in lockstep.
/// </summary>
public interface ICqrsNotificationRegistry
{
    /// <summary>
    ///     The notification types that have at least one registered in-process handler, in a deterministic order.
    /// </summary>
    IReadOnlyList<Type> HandledNotificationTypes { get; }

    /// <summary>
    ///     Returns <see langword="true" /> when the given notification type has a stable outbox name (a
    ///     <c>[NotificationName]</c>), i.e. it can be durably stored and replayed via the outbox; otherwise
    ///     <see langword="false" />, meaning it can only be dispatched in-process.
    /// </summary>
    bool HasStableName(Type notificationType);
}
