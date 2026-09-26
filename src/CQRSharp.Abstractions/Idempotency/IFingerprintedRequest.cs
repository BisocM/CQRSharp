namespace CQRSharp;

/// <summary>
///     An idempotent request that computes its own payload fingerprint: the digest the idempotency behavior stores with
///     the claim and compares on a duplicate, so an idempotency key reused for a <em>different</em> request is rejected
///     (<see cref="IdempotencyKeyMismatchException" />) instead of replayed.
/// </summary>
/// <remarks>
///     Implement this when the automatic fingerprint the source generator derives from the request's properties is
///     unavailable (the generator reports CQRGEN014) or wrong for your request — for instance when a property such as
///     a client timestamp legitimately differs between retries and must not count. The fingerprint must be stable across
///     processes, cultures and deployments: render numbers and dates with the invariant culture. The behavior scopes it
///     by the request's type, as it does the generated ones, so two request types that return the same string under one
///     key are a mismatch rather than a replay of each other; it stores a SHA-256 digest of the two together, never the
///     text itself, so any length will do.
/// </remarks>
public interface IFingerprintedRequest : IIdempotentRequest
{
    /// <summary>
    ///     A stable rendering of the request's meaningful payload (for instance
    ///     <c>FormattableString.Invariant($"{OrderId}:{Amount}")</c>); two requests of the same type that must count as
    ///     the same one return the same string. Empty or <c>null</c> disables the payload check.
    /// </summary>
    string? Fingerprint { get; }
}
