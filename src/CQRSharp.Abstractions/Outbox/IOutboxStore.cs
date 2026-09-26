namespace CQRSharp.Persistence;

/// <summary>
///     The persistence contract of the outbox: where durable notifications are stored, one message per handler, until
///     the outbox processor delivers them. Implement it to keep the outbox in your own storage. A store that writes through the unit of work's own transaction
///     (<see cref="JoinsUnitOfWork" />) makes the messages atomic with the business data; any other store is written
///     right after the unit of work commits.
/// </summary>
/// <remarks>
///     <para>
///         <b>Atomicity.</b> Notifications published inside a request, or by a handler while the outbox processor
///         delivers a message to it, reach the store when that request or delivery succeeds; for one that runs in a
///         unit of work, when that unit of work commits. A store that <see cref="JoinsUnitOfWork">joins</see> the
///         transaction is written just before the commit, inside it: the messages commit or roll back with the data
///         they announce. Any other store is written right after a successful commit, so a rolled-back request or
///         delivery never publishes and no message is claimable before its data is visible; the price is that a crash
///         (or a store failure, which is logged) in the instant after the commit loses those messages. A request or
///         delivery that fails publishes nothing, so a retried one publishes once.
///     </para>
///     <para>
///         The outbox provides <b>at-least-once</b> delivery: a message may be dispatched more than once if a process
///         crashes after dispatch but before the message is marked processed. Notification handlers reached through the
///         outbox should therefore be idempotent. Concurrent double-processing within a single run is prevented by the
///         claim semantics of <see cref="ClaimPendingAsync" />.
///     </para>
///     <para>
///         A message is <b>one delivery to one handler</b> (<see cref="OutboxMessage.HandlerName" />): publishing a
///         notification stores one message per subscribed handler, and each is claimed, retried and dead-lettered on
///         its own. The store treats the handler name as opaque data.
///     </para>
///     <para>
///         <b>Order.</b> Messages are claimed oldest-first by <see cref="OutboxMessage.CreatedAt" />; messages with the
///         same timestamp are claimed in the order the store received them. A message with a
///         <see cref="OutboxMessage.PartitionKey" /> is additionally <b>held back while an earlier message with the same
///         key and the same handler is still pending or in progress</b> (backing off, leased, or simply not yet
///         claimed), so the deliveries of one key to one handler happen strictly in order, and <b>never while another
///         message of that partition is being delivered</b> (a claim whose lease has not expired) — even a message that
///         sorts earlier, such as a requeued dead letter, waits for the delivery in flight. A processed or dead-lettered
///         message no longer holds anything back. Messages without a key, with different keys, or for different handlers
///         are independent of each other.
///     </para>
/// </remarks>
public interface IOutboxStore
{
    /// <summary>
    ///     Whether <see cref="StoreAsync" /> writes through the transaction the scope's <see cref="IUnitOfWork" /> has
    ///     open, so stored messages commit and roll back with it. Read while that transaction is open, right before the
    ///     pipeline decides whether to store a request's notifications before its commit (inside the transaction) or
    ///     after it. A store that writes anywhere else (another connection, another database, a cache) returns
    ///     <c>false</c>; <c>true</c> for a store whose writes do not roll back with the transaction publishes the
    ///     notifications of work whose commit then fails.
    /// </summary>
    bool JoinsUnitOfWork { get; }

    /// <summary>
    ///     Stores a collection of notifications in the outbox persistence layer: inside the unit of work's transaction
    ///     when the store <see cref="JoinsUnitOfWork">joins</see> it, otherwise on its own.
    /// </summary>
    /// <param name="messages">The outbox messages to store, in publication order.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task StoreAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken);

    /// <summary>
    ///     Atomically <b>claims</b> a batch of due messages for processing: takes a lease on each and returns it with the
    ///     claim every later operation on it must present.
    /// </summary>
    /// <remarks>
    ///     Implementations must transition each returned message from <see cref="OutboxMessageStatus.Pending" /> to
    ///     <see cref="OutboxMessageStatus.InProgress" /> as part of an atomic claim, so that two concurrent processors
    ///     never receive the same message. A message is "due" when its <see cref="OutboxMessage.NextRetryAt" /> is null
    ///     or in the past, and — when it carries a <see cref="OutboxMessage.PartitionKey" /> — no earlier message with
    ///     the same key and handler is still pending or in progress and no message of that partition holds a live
    ///     lease. Messages are returned oldest-first by
    ///     <see cref="OutboxMessage.CreatedAt" />, then in the order they were stored. Implementations may additionally
    ///     reclaim messages that have been stuck in <see cref="OutboxMessageStatus.InProgress" /> beyond a visibility
    ///     timeout (e.g. after a crash); such a reclaim issues a new claim, so the previous claimant's no longer matches.
    /// </remarks>
    /// <param name="batchSize">The maximum number of messages to claim.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    ///     The claimed messages, oldest first, each in the <see cref="OutboxMessageStatus.InProgress" /> state together
    ///     with its <see cref="ClaimedOutboxMessage.Claim" />; empty when nothing is due.
    /// </returns>
    Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimPendingAsync(int batchSize, CancellationToken cancellationToken);

    /// <summary>
    ///     Marks a claimed message as successfully processed.
    /// </summary>
    /// <param name="claim">The claim <see cref="ClaimPendingAsync" /> issued for the message.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    ///     <c>true</c> when the message was finalized; <c>false</c> when it is unknown, already terminal, or the claim was
    ///     lost (the lease expired and another processor claimed the message). Never throws for those cases.
    /// </returns>
    Task<bool> MarkAsProcessedAsync(OutboxClaim claim, CancellationToken cancellationToken);

    /// <summary>
    ///     Records a failed delivery attempt for a claimed message and returns it for a later retry.
    /// </summary>
    /// <remarks>
    ///     Implementations must increment <see cref="OutboxMessage.AttemptCount" />, store <paramref name="error" />
    ///     in <see cref="OutboxMessage.LastError" />, set <see cref="OutboxMessage.NextRetryAt" /> to
    ///     <paramref name="nextRetryAt" />, and return the message to <see cref="OutboxMessageStatus.Pending" /> so it
    ///     becomes eligible again once the back-off elapses. The persisted attempt count is what lets retry limits
    ///     survive a process restart. A report under a lost claim must change nothing: the message belongs to another
    ///     processor now, and moving it back to pending would put it in two hands at once.
    /// </remarks>
    /// <param name="claim">The claim <see cref="ClaimPendingAsync" /> issued for the message.</param>
    /// <param name="error">The error message or exception details from the failed attempt.</param>
    /// <param name="nextRetryAt">The earliest UTC time the message may be claimed again, or null for immediately.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    ///     The new <see cref="OutboxMessage.AttemptCount" /> after incrementing; 0 if the message was not found, is
    ///     already terminal, or the claim was lost.
    /// </returns>
    Task<int> IncrementAttemptAsync(OutboxClaim claim, string? error, DateTime? nextRetryAt, CancellationToken cancellationToken);

    /// <summary>
    ///     Permanently marks a claimed message as failed (a dead letter) after its retry attempts were exhausted.
    /// </summary>
    /// <remarks>
    ///     Implementations must count the attempt that exhausted the budget (increment
    ///     <see cref="OutboxMessage.AttemptCount" />), store <paramref name="error" /> in
    ///     <see cref="OutboxMessage.LastError" />, set <see cref="OutboxMessage.FailedAt" /> to now and clear
    ///     <see cref="OutboxMessage.NextRetryAt" />, so a dead letter reads the same whichever store holds it. A dead
    ///     letter is terminal: it releases the messages it was holding back in its partition, so one poison message
    ///     stops one delivery, not every later delivery for that key.
    /// </remarks>
    /// <param name="claim">The claim <see cref="ClaimPendingAsync" /> issued for the message.</param>
    /// <param name="error">The error message or exception details.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><c>true</c> when the message was dead-lettered; <c>false</c> when unknown, already terminal, or the claim was lost.</returns>
    Task<bool> MarkAsFailedAsync(OutboxClaim claim, string? error, CancellationToken cancellationToken);

    /// <summary>
    ///     Extends the lease on a claimed message by the store's visibility timeout, counted from now.
    /// </summary>
    /// <remarks>
    ///     A processor claims a batch at once and works through it a few deliveries at a time
    ///     (<c>MaxDegreeOfParallelism</c>), so a message can wait behind the others for a good part of its lease. Just
    ///     before dispatching a message, once half of the lease it was claimed with has elapsed, the processor renews its
    ///     claim; it never renews while the handler runs. A renewal is refused once the lease has run out, even when no
    ///     other processor has claimed the message since: the message has been claimable from that moment, and a claim
    ///     may already have let an earlier message of its partition through, which a renewed delivery would then run
    ///     beside.
    /// </remarks>
    /// <param name="claim">The claim to extend.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    ///     The claim to use from now on (its token may differ from the one passed in), or <c>null</c> when the claim was
    ///     lost or its lease had already run out — the message must then be left alone.
    /// </returns>
    Task<OutboxClaim?> RenewAsync(OutboxClaim claim, CancellationToken cancellationToken);

    /// <summary>
    ///     Hands a claimed message back undelivered, to be claimed again no earlier than <paramref name="notBefore" />,
    ///     without counting a delivery attempt. The processor defers a message it cannot deliver itself — one whose
    ///     notification or handler this instance does not know — because another instance of a fleet running mixed
    ///     versions (a rolling deploy) may know it.
    /// </summary>
    /// <remarks>
    ///     Implementations must return the message to <see cref="OutboxMessageStatus.Pending" /> with
    ///     <see cref="OutboxMessage.NextRetryAt" /> set to <paramref name="notBefore" /> and <paramref name="reason" />
    ///     stored in <see cref="OutboxMessage.LastError" />, leave <see cref="OutboxMessage.AttemptCount" /> unchanged
    ///     (spending the attempt budget would dead-letter the message early on the instance that can deliver it), end
    ///     the claim, and let the message take its place in its partition again exactly as
    ///     <see cref="IncrementAttemptAsync" /> does. A deferral under a lost claim must change nothing.
    /// </remarks>
    /// <param name="claim">The claim <see cref="ClaimPendingAsync" /> issued for the message.</param>
    /// <param name="notBefore">The earliest UTC time the message may be claimed again.</param>
    /// <param name="reason">Why the message was deferred, kept as its <see cref="OutboxMessage.LastError" />.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    ///     <c>true</c> when the message was deferred; <c>false</c> when it is unknown, already terminal, or the claim was
    ///     lost. Never throws for those cases.
    /// </returns>
    Task<bool> DeferAsync(OutboxClaim claim, DateTime notBefore, string? reason, CancellationToken cancellationToken);

    /// <summary>
    ///     Gives claimed-but-undispatched messages back, making them immediately claimable again without counting an
    ///     attempt. Used on shutdown, so a restart does not have to wait out the visibility timeout for the rest of the
    ///     batch the stopping processor had claimed.
    /// </summary>
    /// <param name="claims">The claims to release. A lost claim is skipped.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task ReleaseAsync(IReadOnlyCollection<OutboxClaim> claims, CancellationToken cancellationToken);

    /// <summary>
    ///     Returns dead-lettered messages (<see cref="OutboxMessageStatus.Failed" />) for inspection, oldest first by
    ///     <see cref="OutboxMessage.FailedAt" />, each complete with its payload and <see cref="OutboxMessage.LastError" />.
    /// </summary>
    /// <param name="limit">The maximum number of messages to return.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The dead letters, oldest first; empty when there are none.</returns>
    Task<IReadOnlyList<OutboxMessage>> GetDeadLettersAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    ///     Gives a dead-lettered message a fresh delivery budget: it returns to <see cref="OutboxMessageStatus.Pending" />
    ///     with <see cref="OutboxMessage.AttemptCount" /> reset to zero, no back-off and no <see cref="OutboxMessage.FailedAt" />
    ///     (<see cref="OutboxMessage.LastError" /> is kept as the record of why it failed), and takes its place in its
    ///     partition again by <see cref="OutboxMessage.CreatedAt" />. The operator's tool once the cause was fixed.
    /// </summary>
    /// <param name="messageId">The message to requeue.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><c>true</c> when the message was requeued; <c>false</c> when it is unknown or not dead-lettered.</returns>
    Task<bool> RequeueAsync(Guid messageId, CancellationToken cancellationToken);

    /// <summary>
    ///     Deletes dead-lettered messages that failed before <paramref name="failedBefore" />.
    /// </summary>
    /// <param name="failedBefore">The UTC cut-off; a dead letter with a <see cref="OutboxMessage.FailedAt" /> at or before it is deleted.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The number of messages deleted.</returns>
    Task<int> PurgeDeadLettersAsync(DateTime failedBefore, CancellationToken cancellationToken);

    /// <summary>
    ///     Measures the backlog: how many messages are still to be delivered (pending or in progress), how many are
    ///     dead-lettered, and how old the oldest undelivered one is. What the outbox gauges and health check report.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The backlog at this instant.</returns>
    Task<OutboxBacklog> GetBacklogAsync(CancellationToken cancellationToken);
}
