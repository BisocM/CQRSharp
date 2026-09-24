namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     The names of everything CQRSharp emits, so telemetry can be wired, and queried, without string literals:
///     <code>
///     builder.Services.AddOpenTelemetry()
///         .WithTracing(t =&gt; t.AddSource(CqrsTelemetry.ActivitySourceNames))
///         .WithMetrics(m =&gt; m.AddMeter(CqrsTelemetry.MeterNames));
///     </code>
///     Nothing is recorded — and nothing is paid for — until a listener subscribes.
/// </summary>
public static class CqrsTelemetry
{
    /// <summary>The activity source for request dispatch, queued execution and outbox delivery spans.</summary>
    public const string ActivitySourceName = "CQRSharp";

    /// <summary>The activity source for the spans of the built-in pipeline behaviors (rate limiting, resilience, unit of work, timeout, …).</summary>
    public const string PipelinesActivitySourceName = "CQRSharp.Pipelines";

    /// <summary>
    ///     The meter for dispatch, notification and outbox instruments. Each service provider owns its own instance of it
    ///     (created through its <c>IMeterFactory</c> when one is registered), so two hosts in one process never report
    ///     each other's measurements.
    /// </summary>
    public const string MeterName = "CQRSharp";

    /// <summary>The meter for the background task queue's instruments (<see cref="QueueInstruments" />).</summary>
    public const string BackgroundTasksMeterName = "CQRSharp.Core.BackgroundTasks";

    /// <summary>Every CQRSharp activity source, for <c>AddSource(params string[])</c>.</summary>
    public static string[] ActivitySourceNames => [ActivitySourceName, PipelinesActivitySourceName];

    /// <summary>Every CQRSharp meter, for <c>AddMeter(params string[])</c>.</summary>
    public static string[] MeterNames => [MeterName, BackgroundTasksMeterName];

    /// <summary>Instrument names on <see cref="MeterName" />.</summary>
    public static class Instruments
    {
        /// <summary>
        ///     Histogram (seconds): how long a command, query or stream took end to end, behaviors included. Tags:
        ///     <see cref="Tags.RequestType" />, <see cref="Tags.RequestKind" /> (<c>command</c> / <c>query</c> /
        ///     <c>stream</c>) and <see cref="Tags.Outcome" /> (<c>success</c> / <c>failure</c>; a command that
        ///     <em>returns</em> a failed result is a failure, and so is a stream its consumer stopped early).
        /// </summary>
        public const string RequestDuration = "cqrsharp.request.duration";

        /// <summary>Counter: notifications published in-process. Tag: <see cref="Tags.NotificationType" />.</summary>
        public const string NotificationsPublished = "cqrsharp.notifications.published";

        /// <summary>
        ///     Counter: outbox messages the processor finished an attempt at. Tags: <see cref="Tags.NotificationName" />,
        ///     <see cref="Tags.NotificationHandler" /> and <see cref="Tags.Outcome" /> (<c>processed</c> /
        ///     <c>duplicate</c> / <c>unrecorded</c> / <c>retry</c> / <c>deferred</c> / <c>dead_letter</c> /
        ///     <c>claim_lost</c> / <c>not_started</c>, the last for a delivery whose claim renewal, inbox check or
        ///     transaction failed before the handler ran).
        /// </summary>
        public const string OutboxMessages = "cqrsharp.outbox.messages";

        /// <summary>Histogram (seconds): how long dispatching one outbox message to its handler took. Same tags as <see cref="OutboxMessages" />.</summary>
        public const string OutboxDispatchDuration = "cqrsharp.outbox.dispatch.duration";

        /// <summary>Gauge: outbox messages still to be delivered (pending or in progress), as last sampled by the processor.</summary>
        public const string OutboxPending = "cqrsharp.outbox.pending";

        /// <summary>Gauge: dead-lettered outbox messages awaiting an operator, as last sampled by the processor.</summary>
        public const string OutboxDeadLetters = "cqrsharp.outbox.dead_letters";

        /// <summary>Gauge (seconds): the age of the oldest undelivered outbox message — how far behind the outbox is.</summary>
        public const string OutboxLag = "cqrsharp.outbox.lag";
    }

    /// <summary>
    ///     Instrument names on <see cref="BackgroundTasksMeterName" />: the background task queue behind
    ///     <see cref="RunMode.Queued" /> dispatch and <c>IBackgroundTaskManager</c>. Each queue (one per host) has its own
    ///     meter of that name.
    /// </summary>
    public static class QueueInstruments
    {
        /// <summary>
        ///     Observable up-down counter: work items waiting in the queue (accepted, not started yet). A withdrawn item
        ///     keeps its place until the consumer reaches it.
        /// </summary>
        public const string Depth = "cqrsharp.queue.depth";

        /// <summary>Counter: work items the queue accepted.</summary>
        public const string Enqueued = "cqrsharp.queue.enqueued";

        /// <summary>
        ///     Counter: queued work items evicted from a full queue to make room for newer work. Tag:
        ///     <see cref="Tags.QueueReason" /> (<c>drop_oldest</c> / <c>drop_newest</c>, the full mode that evicted it).
        /// </summary>
        public const string Evicted = "cqrsharp.queue.evicted";

        /// <summary>
        ///     Counter: work items the queue refused. Tag: <see cref="Tags.QueueReason" /> (<c>full</c>: the queue was
        ///     full and its full mode is <c>DropWrite</c>; <c>closed</c>: the queue no longer accepts work).
        /// </summary>
        public const string Rejected = "cqrsharp.queue.rejected";

        /// <summary>Histogram (seconds): how long a work item waited in the queue before it started.</summary>
        public const string WaitDuration = "cqrsharp.queue.wait.duration";
    }

    /// <summary>
    ///     The attribute keys CQRSharp puts on its spans and measurements. A key means the same thing, with the same
    ///     value, wherever it appears.
    /// </summary>
    public static class Tags
    {
        /// <summary>
        ///     The request's type: its full name, generic arguments included, without assembly names (for example
        ///     <c>Shop.Orders.CreateOrder+Command</c>). On every dispatch and pipeline behavior span, and on
        ///     <see cref="Instruments.RequestDuration" />.
        /// </summary>
        public const string RequestType = "cqrsharp.request.type";

        /// <summary>What the request is: <c>command</c>, <c>query</c> or <c>stream</c>. On <see cref="Instruments.RequestDuration" />.</summary>
        public const string RequestKind = "cqrsharp.request.kind";

        /// <summary>How the measured operation ended; the values are listed with each instrument that carries it.</summary>
        public const string Outcome = "cqrsharp.outcome";

        /// <summary>
        ///     The type of a notification published in-process, in the form of <see cref="RequestType" />. On
        ///     <see cref="Instruments.NotificationsPublished" />.
        /// </summary>
        public const string NotificationType = "cqrsharp.notification.type";

        /// <summary>
        ///     The stable name an outbox message stores its notification under (<c>[NotificationName]</c>). On the outbox
        ///     dispatch span and the outbox instruments.
        /// </summary>
        public const string NotificationName = "cqrsharp.notification.name";

        /// <summary>
        ///     The stable name of the handler an outbox message is addressed to. On the outbox dispatch span and the outbox
        ///     instruments.
        /// </summary>
        public const string NotificationHandler = "cqrsharp.notification.handler";

        /// <summary>The partition key an outbox message is delivered in order within, when it has one. On the outbox dispatch span.</summary>
        public const string PartitionKey = "cqrsharp.partition_key";

        /// <summary>The isolation level a unit-of-work transaction was begun with. On the <c>UoW.Transaction</c> span.</summary>
        public const string IsolationLevel = "cqrsharp.transaction.isolation_level";

        /// <summary>The request's time budget, in milliseconds. On the <c>Timeout.Guard</c> span.</summary>
        public const string TimeoutMilliseconds = "cqrsharp.timeout_ms";

        /// <summary>The most retries the request may get. On the <c>Resilience.Operation</c> span.</summary>
        public const string MaxRetries = "cqrsharp.resilience.max_retries";

        /// <summary>The back-off before the latest retry, in milliseconds. On the <c>Resilience.Operation</c> span.</summary>
        public const string RetryDelayMilliseconds = "cqrsharp.resilience.retry_delay_ms";

        /// <summary>The user id a request is rate limited by (<c>IRateLimitedContext.UserId</c>). On the <c>RateLimiting.Check</c> span.</summary>
        public const string RateLimitUserId = "cqrsharp.ratelimit.user_id";

        /// <summary>
        ///     How long a rate-limited caller must wait for its next token, in milliseconds. On the <c>RateLimiting.Check</c>
        ///     span of a rejected request.
        /// </summary>
        public const string RateLimitRetryAfterMilliseconds = "cqrsharp.ratelimit.retry_after_ms";

        /// <summary>
        ///     Why the background task queue evicted or refused a work item. On <see cref="QueueInstruments.Evicted" /> and
        ///     <see cref="QueueInstruments.Rejected" />, whose summaries list the values.
        /// </summary>
        public const string QueueReason = "cqrsharp.queue.reason";
    }

    /// <summary>The value of <see cref="Tags.RequestType" /> and <see cref="Tags.NotificationType" /> for a type.</summary>
    internal static string TypeName(Type type) => type.ToString();
}
