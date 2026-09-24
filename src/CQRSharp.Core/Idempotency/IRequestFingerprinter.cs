using System.ComponentModel;

namespace CQRSharp.Core.Idempotency;

/// <summary>
///     Computes the payload fingerprint of an idempotent request: the digest the idempotency behavior stores with the
///     request's claim and compares on a duplicate, so a key reused for a different request is rejected instead of
///     replayed. The source generator emits one per module for every <see cref="IIdempotentRequest" /> whose properties
///     it can serialize (a SHA-256 over their JSON rendering); a request that implements
///     <see cref="IFingerprintedRequest" /> supplies its own and is not consulted here. Public only for those generated
///     fingerprinters to implement.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IRequestFingerprinter
{
    /// <summary>Computes the fingerprint of <paramref name="request" /> when this fingerprinter knows its type.</summary>
    /// <param name="request">The request.</param>
    /// <param name="fingerprint">The digest; stable across processes.</param>
    /// <returns><c>false</c> when the request's type is not one this fingerprinter can fingerprint.</returns>
    bool TryFingerprint(IRequest request, out string fingerprint);
}
