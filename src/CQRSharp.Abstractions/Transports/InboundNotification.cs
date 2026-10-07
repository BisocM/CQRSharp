using System.Diagnostics.CodeAnalysis;

namespace CQRSharp.Transports;

/// <summary>A notification a transport received, handed to <see cref="INotificationIntake.AcceptAsync" />.</summary>
/// <param name="Transport">The name of the transport it came through; the <c>cqrsharp.transport</c> attribute of its telemetry.</param>
/// <param name="Source">
///     Where it came from within the transport, and the namespace its <paramref name="MessageId" /> is unique in: the
///     inbox records it under this name, so two sources never deduplicate each other's messages. At most 256 characters;
///     for example <c>rabbitmq:billing</c> for a queue.
/// </param>
/// <param name="MessageId">
///     The id the sender gave the message, the same on every redelivery, or <see langword="null" /> when it has none, which
///     turns deduplication off for it. An id that is not a <see cref="Guid" /> is mapped to one deterministically.
/// </param>
/// <param name="NotificationName">The stable name the notification is read back by; empty when the message carried none.</param>
/// <param name="Payload">The serialized notification.</param>
/// <param name="SentAt">
///     When the notification was published, or, without that, when it was first received: how old it is decides how long a
///     notification this application does not know is held back for a newer instance that may know it.
/// </param>
/// <param name="TraceParent">The W3C <c>traceparent</c> the sender propagated, or <see langword="null" />.</param>
/// <param name="TraceState">The W3C <c>tracestate</c> the sender propagated, or <see langword="null" />.</param>
[Experimental(TransportExperiment.DiagnosticId, UrlFormat = TransportExperiment.UrlFormat)]
public sealed record InboundNotification(
    string Transport,
    string Source,
    string? MessageId,
    string NotificationName,
    byte[] Payload,
    DateTime? SentAt,
    string? TraceParent,
    string? TraceState);
