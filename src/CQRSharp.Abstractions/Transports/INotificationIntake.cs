using System.Diagnostics.CodeAnalysis;

namespace CQRSharp.Transports;

/// <summary>
///     Takes a notification received from outside the process into the local outbox, where the outbox processor delivers
///     it to the local handlers with their usual attempts, back-off, dead letters, ordering and inbox. What a transport's
///     consumer calls for every message it receives, before it acknowledges the message to its source.
/// </summary>
/// <remarks>
///     <para>
///         It deduplicates by <see cref="InboundNotification.MessageId" /> through the <c>IInboxStore</c>, reads the
///         notification back through the application's serializer, and stores one outbox message per local handler that
///         subscribes to it; never one for a transport, so a service that both takes in and forwards a notification does not
///         send back what it received. When the scope has a unit of work and both the outbox store and the inbox join its
///         transaction, the messages and the dedupe record commit together, and the intake is exactly-once; otherwise the
///         messages are stored first and recorded right after, so a crash between the two takes the notification in again,
///         and it is never lost.
///     </para>
///     <para>
///         Resolve it from a DI scope the caller creates for the one message, and acknowledge the message to its source only
///         after <see cref="AcceptAsync" /> returned: the notification is durable then. An exception (the store or the inbox
///         is unreachable) leaves nothing half-done that a later call could not repeat.
///     </para>
/// </remarks>
[Experimental(TransportExperiment.DiagnosticId, UrlFormat = TransportExperiment.UrlFormat)]
public interface INotificationIntake
{
    /// <summary>Takes one received notification in.</summary>
    /// <param name="message">What was received.</param>
    /// <param name="cancellationToken">A token to cancel the intake.</param>
    /// <returns>What became of it, and so whether to acknowledge, hold or reject it at its source.</returns>
    Task<IntakeResult> AcceptAsync(InboundNotification message, CancellationToken cancellationToken);
}
