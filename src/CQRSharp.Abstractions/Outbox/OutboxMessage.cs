namespace CQRSharp.Persistence;

/// <summary>
///     One delivery of a notification to one handler, as stored in the outbox. Publishing a durable notification
///     stores one message per subscribed handler, so every handler has its own attempts, back-off and dead letter and
///     a failing handler never makes its siblings run again.
/// </summary>
/// <param name="Id">The unique identifier of this message (this delivery).</param>
/// <param name="NotificationType">
///     The stable name of the notification, as given by <see cref="INotificationSerializer.TryGetNotificationName" />.
/// </param>
/// <param name="HandlerName">
///     The stable name of the handler this message is delivered to: the handler type's namespace-qualified name, or
///     the name given with <c>[NotificationHandlerName]</c>.
/// </param>
/// <param name="Payload">The serialized content of the notification.</param>
/// <param name="CreatedAt">The UTC timestamp when the message was created; the primary FIFO ordering key.</param>
/// <param name="Status">The current processing status of the message.</param>
/// <param name="ProcessedAt">The UTC timestamp when the message was processed, or null if not yet processed.</param>
/// <param name="LastError">Error information from the most recent failed delivery attempt, if any.</param>
/// <param name="AttemptCount">
///     The number of failed delivery attempts recorded so far (a dead letter counts the attempt that exhausted the
///     budget; a deferral or a release counts none). Persisted so that the attempt limit survives process restarts
///     (rather than being tracked only in memory by the processor).
/// </param>
/// <param name="NextRetryAt">
///     The earliest UTC time at which a failed message becomes eligible to be claimed again, or null to make it
///     immediately eligible. Used to back off between retries.
/// </param>
/// <param name="TraceParent">
///     The W3C <c>traceparent</c> of the request that produced this message, captured at enqueue time so the outbox
///     dispatch span can link back to the originating trace. Null when no trace was active.
/// </param>
/// <param name="PartitionKey">
///     The ordering key, or null for a message that may be delivered in any order relative to the others. Messages
///     that share a partition key <em>and</em> a handler are delivered strictly in order: a store never hands out the
///     next one while an earlier one is still pending or in progress.
/// </param>
/// <param name="NotificationId">
///     The identifier shared by every message a single publish produced (one per handler), so the deliveries of one
///     notification can be correlated. Null on a message that was not produced by a publish (one built by hand).
/// </param>
/// <param name="FailedAt">
///     The UTC timestamp when the message was dead-lettered (<see cref="OutboxMessageStatus.Failed" />), or null. What
///     <see cref="IOutboxStore.PurgeDeadLettersAsync" /> and a store's dead-letter retention measure age by.
/// </param>
public sealed record OutboxMessage(
    Guid Id,
    string NotificationType,
    string HandlerName,
    byte[] Payload,
    DateTime CreatedAt,
    OutboxMessageStatus Status,
    DateTime? ProcessedAt,
    string? LastError,
    int AttemptCount = 0,
    DateTime? NextRetryAt = null,
    string? TraceParent = null,
    string? PartitionKey = null,
    Guid? NotificationId = null,
    DateTime? FailedAt = null
);
