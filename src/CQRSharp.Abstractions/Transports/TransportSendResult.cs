using System.Diagnostics.CodeAnalysis;

namespace CQRSharp.Transports;

/// <summary>
///     What became of one send through an <see cref="INotificationTransport" />, and so what the outbox does with the
///     message: <see cref="Sent" /> marks it processed; <see cref="Unavailable" /> sends it again later without charging an
///     attempt, so an outage of the destination never dead-letters a message; <see cref="Rejected" /> charges an attempt
///     and backs off, and dead-letters the message once the attempts run out, or at once when the rejection is permanent.
/// </summary>
/// <remarks>
///     The default value has no <see cref="Status" /> and is treated as a rejection: a transport must return one of the
///     three results.
/// </remarks>
[Experimental(TransportExperiment.DiagnosticId, UrlFormat = TransportExperiment.UrlFormat)]
public readonly record struct TransportSendResult
{
    private TransportSendResult(TransportSendStatus status, string? reason, TimeSpan? retryAfter, bool permanent)
    {
        Status = status;
        Reason = reason;
        RetryAfter = retryAfter;
        Permanent = permanent;
    }

    /// <summary>How the send ended.</summary>
    public TransportSendStatus Status { get; }

    /// <summary>Why the send did not succeed, recorded as the message's last error; <see langword="null" /> for <see cref="Sent" />.</summary>
    public string? Reason { get; }

    /// <summary>
    ///     For <see cref="Unavailable" />, the soonest the send is worth trying again; <see langword="null" /> uses the outbox
    ///     processor's retry back-off.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>For <see cref="Rejected" />, whether no later attempt can succeed, so the message is dead-lettered at once.</summary>
    public bool Permanent { get; }

    /// <summary>The destination took the message: it is now the destination's responsibility.</summary>
    public static TransportSendResult Sent { get; } = new(TransportSendStatus.Sent, null, null, false);

    /// <summary>
    ///     The destination could not be reached, or could not take the message for a reason that passes on its own (it is
    ///     down, overloaded, or did not confirm in time). The message is sent again later and no attempt is charged, so a
    ///     send the destination did take may be repeated.
    /// </summary>
    /// <param name="reason">Why; recorded as the message's last error.</param>
    /// <param name="retryAfter">The soonest a new send is worth trying, or <see langword="null" /> for the processor's back-off.</param>
    /// <returns>The result.</returns>
    public static TransportSendResult Unavailable(string reason, TimeSpan? retryAfter = null)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required.", nameof(reason));
        if (retryAfter < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryAfter), retryAfter, "The delay must not be negative.");
        return new TransportSendResult(TransportSendStatus.Unavailable, reason, retryAfter, false);
    }

    /// <summary>
    ///     The destination refused the message (it cannot be routed, the destination does not exist, access is denied). An
    ///     attempt is charged; the message backs off, and is dead-lettered once the attempts run out, or at once when
    ///     <paramref name="permanent" /> says no attempt can succeed (the message is larger than the destination accepts).
    /// </summary>
    /// <param name="reason">Why; recorded as the message's last error.</param>
    /// <param name="permanent">Whether no later attempt can succeed.</param>
    /// <returns>The result.</returns>
    public static TransportSendResult Rejected(string reason, bool permanent = false)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required.", nameof(reason));
        return new TransportSendResult(TransportSendStatus.Rejected, reason, null, permanent);
    }
}
