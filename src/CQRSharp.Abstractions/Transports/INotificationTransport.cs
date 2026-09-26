using System.Diagnostics.CodeAnalysis;

namespace CQRSharp.Transports;

/// <summary>
///     A destination outside the process that durable notifications are forwarded to through the outbox, such as a
///     message broker. A transport is one more outbox subscriber: a published notification it <see cref="Routes" /> is
///     stored with one extra outbox message addressed to its <see cref="Name" />, beside the messages of the local
///     handlers, in the same write (and, for a store that joins the unit of work, the same transaction). The outbox
///     processor hands that message to <see cref="SendAsync" /> with its own attempts, back-off and dead letter, so a
///     transport that fails never makes a local handler run again, and a local handler that fails never sends again.
/// </summary>
/// <remarks>
///     <para>
///         A transport is registered as a singleton <see cref="INotificationTransport" />, typically through
///         <c>OutboxStoreBuilder.AddTransport(...)</c>. The processor forwards a transport's messages without
///         deserializing them: the stored payload is what is sent, byte for byte, and no pipeline behavior and no inbox
///         take part in a send.
///     </para>
///     <para>
///         The transport contract suite (<c>NotificationTransportContractTests</c> in <c>CQRSharp.Testing.Xunit.V3</c>)
///         checks an implementation against these rules.
///     </para>
/// </remarks>
[Experimental(TransportExperiment.DiagnosticId, UrlFormat = TransportExperiment.UrlFormat)]
public interface INotificationTransport
{
    /// <summary>
    ///     The name the transport's outbox messages are addressed to (their <c>HandlerName</c>). It shares the namespace of
    ///     the notification handler names, so it must differ from every handler's and every other transport's name
    ///     (<c>CQRCONF015</c>), be at most 256 characters, and never change while messages addressed to it may be stored.
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     What the transport's configuration declares it forwards and takes in, for CQRSharp's configuration checks
    ///     (<c>CQRCONF014</c>, <c>CQRCONF016</c>, <c>CQRCONF017</c>). Read at startup and when the transports are first
    ///     used; it must not change afterwards.
    /// </summary>
    NotificationTransportDeclaration Declaration { get; }

    /// <summary>
    ///     Whether a published notification is forwarded through this transport. Asked once per notification type and
    ///     service provider, and the answer is kept, so it must depend on its arguments alone.
    /// </summary>
    /// <param name="notificationName">The stable name the notification is stored under.</param>
    /// <param name="notificationType">The notification's runtime type.</param>
    /// <returns><see langword="true" /> to store an outbox message for this transport with every publish of the type.</returns>
    bool Routes(string notificationName, Type notificationType);

    /// <summary>
    ///     Sends one stored notification. Called by the outbox processor under the message's lease, and, for messages that
    ///     share a partition key, one at a time and in their stored order: the next is handed out only once this one is
    ///     settled. Whatever it returns, the message is never lost: only <see cref="TransportSendResult.Sent" /> removes it
    ///     from the pending messages.
    /// </summary>
    /// <remarks>
    ///     An exception counts as <see cref="TransportSendResult.Rejected" />: an attempt is charged. A cancellation of
    ///     <paramref name="cancellationToken" /> is the host stopping: the message is handed back undelivered and sent again
    ///     later, so a send the destination already took may be repeated, with the same <see cref="OutboundNotification.MessageId" />.
    /// </remarks>
    /// <param name="message">The stored notification.</param>
    /// <param name="cancellationToken">Cancelled when the host stops.</param>
    /// <returns>What became of the send.</returns>
    Task<TransportSendResult> SendAsync(OutboundNotification message, CancellationToken cancellationToken);
}
