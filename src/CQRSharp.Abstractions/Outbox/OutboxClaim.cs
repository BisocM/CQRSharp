namespace CQRSharp.Persistence;

/// <summary>
///     Proof that a processor currently holds the lease on an outbox message. <see cref="IOutboxStore.ClaimPendingAsync" />
///     issues one with every message it claims, and every later operation on that message must present it.
/// </summary>
/// <remarks>
///     The lease is what makes the outbox safe with several processors: a message left in progress by a processor that
///     crashed (or stalled past the lease) is handed to another one, under a <em>new</em> claim. The old claim then no
///     longer matches, so a late finalize or failed-attempt report from the first processor is rejected instead of
///     corrupting the state the second one is working from.
/// </remarks>
/// <param name="MessageId">The claimed message.</param>
/// <param name="Token">
///     An opaque value chosen by the store that changes whenever the message is claimed again. Compare it for equality
///     only; its format is the store's own business.
/// </param>
/// <param name="LeasedUntil">
///     When (UTC) the lease expires and the message may be claimed by someone else. A processor that needs longer calls
///     <see cref="IOutboxStore.RenewAsync" /> before then.
/// </param>
public readonly record struct OutboxClaim(Guid MessageId, string Token, DateTime LeasedUntil);
