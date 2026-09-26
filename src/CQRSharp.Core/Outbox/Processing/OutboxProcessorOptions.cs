namespace CQRSharp;

/// <summary>
///     Options for the outbox processor: the background service every <c>AddCqrsGenerated</c> host runs, which claims due
///     outbox messages and delivers each to the handler it is addressed to. It idles while the outbox is off. Invalid
///     values fail at host start.
/// </summary>
public sealed class OutboxProcessorOptions
{
    /// <summary>
    ///     How long the processor waits after a poll that found nothing due. While messages are due it claims batch after
    ///     batch without waiting (a backlog, the next message of a partition whose head was just delivered), and a
    ///     message stored by this process wakes it at once. So the interval bounds how late the processor notices a
    ///     message stored by another instance, or one whose retry back-off or deferral has run out. Must be greater than
    ///     zero. Defaults to 5 seconds.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     The most messages one claim takes. Must be greater than zero. Defaults to 100.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    ///     The total number of delivery attempts a message gets, the first one included, before a failing message is
    ///     dead-lettered: 3 (the default) means one attempt and at most two retries, 1 means a failure is never retried.
    ///     Must be at least 1. The count is persisted with the message, so it survives restarts; a deferral (see
    ///     <see cref="UnknownRecipientGracePeriod" />) is not an attempt.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    ///     How long a message this instance cannot deliver — its notification name or handler name is unknown here — is
    ///     left for other instances before it is dead-lettered, measured from the message's creation. In a fleet running
    ///     mixed versions (a rolling deploy that adds a durable notification or a handler), an instance still on the
    ///     previous version claims messages only a newer instance can deliver: it defers them, without counting an
    ///     attempt, so an instance that knows them delivers them. A message nothing has delivered once the period has
    ///     passed is dead-lettered with the reason, which is how a handler that really was removed or renamed surfaces.
    ///     Must be greater than zero; a rollout that runs mixed versions for longer needs a longer period. Defaults to
    ///     1 hour.
    /// </summary>
    public TimeSpan UnknownRecipientGracePeriod { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    ///     How many messages of one batch are delivered at the same time. Each delivery runs in its own DI scope, and a
    ///     batch never holds two messages of one partition, so ordering is preserved at any degree. Defaults to 1
    ///     (one message at a time); must be at least 1. Raise it when handlers spend their time waiting on I/O.
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = 1;

    /// <summary>The back-off between the delivery attempts of a failing message.</summary>
    public OutboxRetryOptions Retry { get; set; } = new();

    /// <summary>
    ///     Whether to record every completed delivery in the <see cref="CQRSharp.Persistence.IInboxStore" /> the outbox
    ///     store registered, and skip a message that was already delivered to its handler. Defaults to <c>true</c>; the
    ///     option only matters when an inbox store is registered.
    /// </summary>
    public bool UseInbox { get; set; } = true;

    /// <summary>
    ///     How often the processor measures the backlog for the <c>cqrsharp.outbox.pending</c>,
    ///     <c>cqrsharp.outbox.dead_letters</c> and <c>cqrsharp.outbox.lag</c> gauges, once something listens to them.
    ///     Must be greater than zero. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan BacklogSampleInterval { get; set; } = TimeSpan.FromSeconds(30);
}
