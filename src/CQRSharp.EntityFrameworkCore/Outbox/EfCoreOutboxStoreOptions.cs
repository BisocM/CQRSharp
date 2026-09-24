namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     Tuning knobs for the EF Core outbox store (<c>AddEntityFrameworkCoreOutboxStore&lt;TContext&gt;()</c>, or
///     <c>UseOutbox(o =&gt; o.UseEntityFrameworkCore&lt;TContext&gt;())</c>) and its retention.
/// </summary>
public sealed class EfCoreOutboxStoreOptions
{
    /// <summary>
    ///     How long a claimed (in-progress) message stays leased before it is considered abandoned and may be
    ///     reclaimed by another processor. A processor that crashes after claiming a message leaves it invisible only
    ///     until this timeout elapses. Must be greater than zero. Default: 5 minutes.
    /// </summary>
    /// <remarks>
    ///     The processor renews a message's lease just before dispatching it once half of the lease has elapsed, but not
    ///     while a handler runs, so a delivery must finish within about half of this timeout: set it comfortably above
    ///     twice your slowest single handler. A lease that runs out while a handler is still running lets another
    ///     processor claim and deliver the message again; with an EF Core unit of work over the same context the inbox
    ///     records the delivery in the handler's transaction, so only one of the two commits. Longer values only delay
    ///     recovery after a crash.
    /// </remarks>
    public TimeSpan VisibilityTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     How many times a batch claim is tried when it loses the row-version race against a competing processor
    ///     before the call gives up and leaves the contested messages for the next poll. At least 1; default 3. Only the
    ///     batch claim retries: every operation under a claim is one conditional update, and one that finds the claim
    ///     lost reports it (<c>false</c>, <c>0</c> or <c>null</c>) rather than retrying.
    /// </summary>
    public int MaxClaimAttempts { get; set; } = 3;

    /// <summary>
    ///     How long a <b>processed</b> message is kept before it is deleted, bounding the table. <c>null</c> keeps
    ///     processed messages forever. Dead-lettered (failed) messages follow <see cref="DeadLetterRetention" /> instead.
    ///     Default: 7 days.
    /// </summary>
    /// <remarks>
    ///     The retention runs as a hosted service registered with the store: when the host starts, then every
    ///     <see cref="PurgeInterval" />, off the processor's claim path, deleting in bounded pages so no single statement
    ///     holds more than a small number of row locks. It is idle while the outbox is off.
    /// </remarks>
    public TimeSpan? ProcessedRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    ///     How long a <b>dead-lettered</b> message is kept before it is deleted, measured from when it failed.
    ///     <c>null</c> (the default) keeps dead letters until they are requeued or purged: they are the record of what
    ///     could not be delivered. A dead letter without a failure time (dead-lettered before the outbox table had the
    ///     column) counts as older than any retention. Purged on the same schedule as processed messages.
    /// </summary>
    public TimeSpan? DeadLetterRetention { get; set; }

    /// <summary>
    ///     How long an inbox record (a completed delivery) is kept, which is how long a redelivery of the same message is
    ///     recognised and skipped. Must comfortably exceed <see cref="VisibilityTimeout" />. Purged on the same schedule
    ///     as processed messages. Default: 7 days.
    /// </summary>
    public TimeSpan InboxRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>The time between two purges of processed messages, dead letters and inbox records. Default: 1 hour.</summary>
    public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromHours(1);
}
