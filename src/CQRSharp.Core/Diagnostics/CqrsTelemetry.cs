using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     The names of everything CQRSharp emits, so telemetry can be wired without string literals:
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
    public const string ActivitySourceName = CqrsActivitySource.Name;

    /// <summary>The activity source for the spans of the opt-in pipeline behaviors (validation, resilience, unit of work, …).</summary>
    public const string PipelinesActivitySourceName = "CQRSharp.Pipelines";

    /// <summary>The meter for dispatch, notification and outbox instruments.</summary>
    public const string MeterName = "CQRSharp";

    /// <summary>The meter for the background task queue's instruments.</summary>
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
        ///     <c>cqrsharp.request.type</c>, <c>cqrsharp.request.kind</c> (<c>command</c> / <c>query</c> / <c>stream</c>),
        ///     <c>cqrsharp.outcome</c> (<c>success</c> / <c>failure</c>; a command that <em>returns</em> a failed result is a failure).
        /// </summary>
        public const string RequestDuration = "cqrsharp.request.duration";

        /// <summary>Counter: notifications published in-process. Tag: <c>cqrsharp.notification.type</c>.</summary>
        public const string NotificationsPublished = "cqrsharp.notifications.published";

        /// <summary>
        ///     Counter: outbox messages the processor finished with. Tags: <c>cqrsharp.notification.type</c> (the stable name),
        ///     <c>cqrsharp.outcome</c> (<c>processed</c> / <c>retry</c> / <c>dead_letter</c> / <c>claim_lost</c>).
        /// </summary>
        public const string OutboxMessages = "cqrsharp.outbox.messages";

        /// <summary>Histogram (seconds): how long dispatching one outbox message to its handlers took. Same tags as <see cref="OutboxMessages" />.</summary>
        public const string OutboxDispatchDuration = "cqrsharp.outbox.dispatch.duration";
    }
}

/// <summary>The instruments behind <see cref="CqrsTelemetry.Instruments" />. Each call site checks <c>Enabled</c> first.</summary>
internal static class CqrsMetrics
{
    private static readonly Meter Meter = new(CqrsTelemetry.MeterName, typeof(CqrsMetrics).Assembly.GetName().Version?.ToString(3));

    public static readonly Histogram<double> RequestDuration =
        Meter.CreateHistogram<double>(CqrsTelemetry.Instruments.RequestDuration, "s", "Duration of a dispatched command, query or stream.");

    public static readonly Counter<long> NotificationsPublished =
        Meter.CreateCounter<long>(CqrsTelemetry.Instruments.NotificationsPublished, "{notification}", "Notifications published in-process.");

    public static readonly Counter<long> OutboxMessages =
        Meter.CreateCounter<long>(CqrsTelemetry.Instruments.OutboxMessages, "{message}", "Outbox messages the processor finished with, by outcome.");

    public static readonly Histogram<double> OutboxDispatchDuration =
        Meter.CreateHistogram<double>(CqrsTelemetry.Instruments.OutboxDispatchDuration, "s", "Duration of dispatching one outbox message to its handlers.");

    public static void RecordRequest(Type requestType, string kind, bool success, TimeSpan elapsed)
    {
        var tags = new TagList
        {
            { "cqrsharp.request.type", requestType.Name },
            { "cqrsharp.request.kind", kind },
            { "cqrsharp.outcome", success ? "success" : "failure" }
        };
        RequestDuration.Record(elapsed.TotalSeconds, tags);
    }

    public static void RecordOutbox(string notificationType, string outcome, TimeSpan elapsed)
    {
        var tags = new TagList
        {
            { "cqrsharp.notification.type", notificationType },
            { "cqrsharp.outcome", outcome }
        };
        OutboxMessages.Add(1, tags);
        OutboxDispatchDuration.Record(elapsed.TotalSeconds, tags);
    }
}
