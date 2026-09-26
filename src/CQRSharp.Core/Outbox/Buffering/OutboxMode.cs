namespace CQRSharp;

/// <summary>
///     Which published notifications go to the outbox (<see cref="OutboxOptions.Mode" />).
/// </summary>
public enum OutboxMode
{
    /// <summary>
    ///     Notifications are dispatched directly in-process and do not use the outbox. This is the default; enable the
    ///     outbox explicitly (e.g. via the builder's <c>UseOutbox(...)</c> verb).
    /// </summary>
    Disabled,

    /// <summary>
    ///     Every notification the serializer names is sent to the outbox for deferred processing by the outbox processor:
    ///     buffered while a request of the scope runs, or while the outbox processor delivers a message to a handler, and
    ///     stored when that succeeds (with its unit of work's commit when it runs in one), or stored at once when published
    ///     outside any of them. The default of the builder's
    ///     <c>UseOutbox(...)</c>. Requires a registered <see cref="CQRSharp.Persistence.IOutboxStore" />; the outbox
    ///     processor, which every host registers, delivers what it stores.
    /// </summary>
    Enabled,

    /// <summary>
    ///     A notification goes to the outbox only while the scope's <see cref="CQRSharp.Persistence.IUnitOfWork" /> has an
    ///     active transaction, so only the notifications of transactional work become durable; any other is dispatched
    ///     in-process at once. Without a registered unit of work nothing ever reaches the outbox.
    /// </summary>
    Transactional
}
