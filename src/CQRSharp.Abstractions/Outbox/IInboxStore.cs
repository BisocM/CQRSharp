namespace CQRSharp.Persistence;

/// <summary>
///     The outbox's <b>inbox</b>: a record of every (message, handler) delivery that completed, so a message that is
///     delivered again after its earlier delivery was recorded — a crash between the record and the processed mark, a
///     lease that ran out after the record landed — is recognised and skipped instead of running the handler twice.
///     Together with the outbox this gives a handler <em>effectively-once</em> delivery: exactly once when the record
///     commits atomically with the handler's own changes (an inbox that <see cref="JoinsUnitOfWork">joins</see> the
///     unit-of-work transaction the delivery runs in); otherwise at-least-once, with a duplicate possible in two
///     windows. One is the instant between the handler's commit and the record: a crash there, or a record that fails,
///     leaves the delivery unrecorded. The other is a lease that expires while a slow handler is still running: another
///     processor claims the message, both deliveries pass <see cref="IsDeliveredAsync" /> before either records, and
///     both handlers run — only one record wins, and nothing undoes the other handler's side effects. Keep the outbox
///     store's visibility timeout well above the slowest handler, or run deliveries in a unit of work whose transaction
///     the inbox joins.
/// </summary>
/// <remarks>
///     The processor checks <see cref="IsDeliveredAsync" /> before running a handler and calls
///     <see cref="RecordDeliveryAsync" /> after it; it renews a message's lease only before dispatching it, never while
///     the handler runs. When the message scope has an <see cref="IUnitOfWork" />, the delivery runs in a transaction
///     of its own: an inbox that joins it records inside that transaction, so the record and the handler's changes
///     commit or roll back together (a duplicate caught at record time takes the losing handler's changes back with
///     it); any other inbox records right after the commit, so a commit that fails never leaves the delivery marked
///     done. What the handler publishes belongs to the delivery the same way: an outbox store that joins the
///     transaction stores it inside it, any other right after the commit and before the record; a failed attempt,
///     and a duplicate that an inbox joining the transaction catches at record time, publish nothing. Without a unit
///     of work the handler's work stands once it returns, and what it published is stored before the record follows
///     it. Only a record inside the delivery's own transaction is part of the attempt; any other record is bookkeeping
///     about work that already stands, so the processor writes it even while the host is stopping, and a record that
///     fails is logged and the delivery still counts as delivered (never as a failed attempt; a redelivery would run
///     the handler again). Records only need to outlive the window in which the message could be delivered again; each
///     store ages them out after its inbox retention. Message ids are unique per handler, so the pair is the record's
///     identity.
/// </remarks>
public interface IInboxStore
{
    /// <summary>
    ///     Whether a record written now goes through the transaction the scope's <see cref="IUnitOfWork" /> has open, so
    ///     it commits and rolls back with the handler's own changes. Read by the processor while that transaction is
    ///     open, right before it decides whether to record inside the transaction or after its commit. A store that
    ///     writes anywhere else (another connection, another database, a cache) returns <c>false</c>, which is always
    ///     safe; <c>true</c> for a store whose writes do not roll back with the transaction lets a failed commit mark a
    ///     delivery done that never happened.
    /// </summary>
    bool JoinsUnitOfWork { get; }

    /// <summary>Whether <paramref name="messageId" /> was already delivered to <paramref name="handlerName" />.</summary>
    /// <param name="messageId">The outbox message.</param>
    /// <param name="handlerName">The handler the message is addressed to.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><c>true</c> when a delivery record exists.</returns>
    Task<bool> IsDeliveredAsync(Guid messageId, string handlerName, CancellationToken cancellationToken);

    /// <summary>
    ///     Records that <paramref name="messageId" /> was delivered to <paramref name="handlerName" />. Atomic: of two
    ///     concurrent recorders exactly one succeeds, which is how a redelivery racing the original is caught even when
    ///     both ran the handler. Outside a transaction it joins, the record writes nothing but itself: the handler's
    ///     own work is never saved through it.
    /// </summary>
    /// <param name="messageId">The outbox message.</param>
    /// <param name="handlerName">The handler the message was delivered to.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><c>true</c> when the record was created; <c>false</c> when one already existed.</returns>
    Task<bool> RecordDeliveryAsync(Guid messageId, string handlerName, CancellationToken cancellationToken);
}
