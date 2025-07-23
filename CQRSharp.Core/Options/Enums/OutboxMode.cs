namespace CQRSharp.Core.Options.Enums;

/// <summary>
///     Defines the behavior for the notification outbox.
/// </summary>
public enum OutboxMode
{
    /// <summary>
    ///     Notifications are dispatched directly in-process and do not use the outbox.
    /// </summary>
    Disabled,

    /// <summary>
    ///     All notifications are sent to the outbox for deferred processing by a background service.
    ///     This requires registering an <see cref="CQRSharp.Abstractions.Data.Interfaces.Outbox.IOutboxStore" /> implementation
    ///     and the outbox processor.
    /// </summary>
    Enabled,

    /// <summary>
    ///     Notifications are sent to the outbox only if they are published within an active database transaction
    ///     managed by a Unit of Work. Otherwise, they are dispatched directly. This is the default behavior.
    /// </summary>
    Transactional
}