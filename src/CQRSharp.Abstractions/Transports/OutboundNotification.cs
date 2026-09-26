using System.Diagnostics.CodeAnalysis;

namespace CQRSharp.Transports;

/// <summary>A stored notification the outbox processor hands to an <see cref="INotificationTransport" />.</summary>
/// <param name="MessageId">
///     The id of the transport's outbox message: the same on every attempt to send it, so a receiver can recognise a
///     repeated send by it. Use it as the message id on the wire.
/// </param>
/// <param name="NotificationId">
///     The id every message of one publish shares (the local handlers' and each transport's), or <see langword="null" />
///     for a message not produced by a publish.
/// </param>
/// <param name="NotificationName">The stable name the notification was stored under; what a receiver reads it back by.</param>
/// <param name="Payload">The serialized notification, exactly as stored. Not to be modified.</param>
/// <param name="CreatedAt">
///     The UTC time the notification takes its place in the delivery order: when it was published, or, for a scheduled
///     publish, when it fell due.
/// </param>
/// <param name="PartitionKey">The ordering key, or <see langword="null" /> for a notification that may be sent in any order.</param>
/// <param name="TraceParent">
///     The W3C <c>traceparent</c> to propagate: the processor's dispatch span when one is recorded, otherwise the span
///     that published the notification; <see langword="null" /> when neither exists.
/// </param>
/// <param name="TraceState">The W3C <c>tracestate</c> of <paramref name="TraceParent" />, or <see langword="null" /> when it has none.</param>
/// <param name="AttemptCount">The failed attempts recorded before this one.</param>
[Experimental(TransportExperiment.DiagnosticId, UrlFormat = TransportExperiment.UrlFormat)]
public sealed record OutboundNotification(
    Guid MessageId,
    Guid? NotificationId,
    string NotificationName,
    byte[] Payload,
    DateTime CreatedAt,
    string? PartitionKey,
    string? TraceParent,
    string? TraceState,
    int AttemptCount);
