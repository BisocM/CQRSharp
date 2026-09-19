namespace CQRSharp.Core.Options.Enums;

/// <summary>
///     Defines the behavior for the notification outbox.
/// </summary>
public enum OutboxMode
{
    /// <summary>
    ///     Notifications are dispatched directly in-process and do not use the outbox. This is the default; enable the
    ///     outbox explicitly (e.g. via the builder's <c>UseOutbox(...)</c> verb).
    /// </summary>
    Disabled,

    /// <summary>
    ///     All notifications are sent to the outbox for deferred processing by a background service.
    ///     This requires registering an <see cref="CQRSharp.Abstractions.Interfaces.Outbox.IOutboxStore" /> implementation
    ///     and the outbox processor.
    /// </summary>
    Enabled,

    /// <summary>
    ///     Notifications are sent to the outbox only if they are published within an active database transaction
    ///     managed by a Unit of Work. Otherwise, they are dispatched directly.
    /// </summary>
    Transactional
}