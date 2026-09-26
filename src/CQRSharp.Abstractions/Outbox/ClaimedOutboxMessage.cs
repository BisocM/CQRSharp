namespace CQRSharp.Persistence;

/// <summary>
///     A message <see cref="IOutboxStore.ClaimPendingAsync" /> handed to a processor, together with the
///     <see cref="OutboxClaim" /> that proves the processor holds its lease. Every later store operation on the message
///     (finalizing, rescheduling, renewing, deferring or releasing it) presents <see cref="Claim" />.
/// </summary>
/// <remarks>
///     A claimed message always carries its claim: the constructor rejects a claim without a token or for another
///     message, so a store that cannot say which lease it took fails where it builds the batch, not later in the
///     processor.
/// </remarks>
public sealed record ClaimedOutboxMessage
{
    /// <summary>Pairs a claimed message with the claim the store took on it.</summary>
    /// <param name="message">The claimed message, in the <see cref="OutboxMessageStatus.InProgress" /> state.</param>
    /// <param name="claim">The lease the store took on <paramref name="message" />.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message" /> is null.</exception>
    /// <exception cref="ArgumentException">
    ///     <paramref name="claim" /> has no token, or names another message than <paramref name="message" />.
    /// </exception>
    public ClaimedOutboxMessage(OutboxMessage message, OutboxClaim claim)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        if (string.IsNullOrEmpty(claim.Token))
            throw new ArgumentException("A claim needs a non-empty token.", nameof(claim));
        if (claim.MessageId != message.Id)
            throw new ArgumentException($"The claim is for message {claim.MessageId}, not for message {message.Id}.", nameof(claim));

        Message = message;
        Claim = claim;
    }

    /// <summary>The claimed message, in the <see cref="OutboxMessageStatus.InProgress" /> state.</summary>
    public OutboxMessage Message { get; }

    /// <summary>The lease the store took on <see cref="Message" />; required by every later operation on it.</summary>
    public OutboxClaim Claim { get; }
}
