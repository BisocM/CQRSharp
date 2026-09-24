using System.Security.Cryptography;
using System.Text;
using CQRSharp.Core.Idempotency;
using CQRSharp.Persistence;

namespace CQRSharp.Pipelines;

/// <summary>The claim step the command/query and streaming idempotency behaviors share.</summary>
internal static class IdempotencyClaims
{
    /// <summary>The request's idempotency key, rejecting an empty one before anything is claimed.</summary>
    public static string KeyOf<TRequest>(IIdempotentRequest request)
    {
        var key = request.IdempotencyKey;
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException(
                $"{typeof(TRequest).Name} implements {nameof(IIdempotentRequest)} but supplied an empty {nameof(IIdempotentRequest.IdempotencyKey)}.");

        return key;
    }

    /// <summary>
    ///     Claims the key with the request's payload fingerprint (its own, or the generated one). A store that answers
    ///     with no claim breaks the contract; that is reported here, before the handler runs, because afterwards its side
    ///     effects would stand under a claim nobody can complete or release.
    /// </summary>
    public static async Task<IdempotencyClaim> ClaimAsync(
        IIdempotencyStore store,
        string key,
        IIdempotentRequest request,
        IRequestFingerprinter? fingerprinter,
        CancellationToken cancellationToken)
    {
        var claim = await store.TryClaimAsync(key, Fingerprint(request, fingerprinter), cancellationToken).ConfigureAwait(false);
        return claim ?? throw new InvalidOperationException(
            $"The idempotency store {store.GetType().FullName} answered {nameof(IIdempotencyStore.TryClaimAsync)} for key '{key}' with no claim. " +
            $"A store answers with {nameof(IdempotencyClaim)}.{nameof(IdempotencyClaim.ClaimedWith)}(token), {nameof(IdempotencyClaim.InProgress)}, " +
            $"{nameof(IdempotencyClaim.PayloadMismatch)} or {nameof(IdempotencyClaim.Completed)}(result).");
    }

    /// <summary>
    ///     Releases a claim whose request did not complete. Never throws: the store's failure is handed back for the
    ///     caller to log, so it can neither replace the request's own failure nor turn a returned failed result into an
    ///     exception.
    /// </summary>
    /// <returns>The store's failure, or <c>null</c> once the claim is released.</returns>
    public static async Task<Exception?> ReleaseAsync(IIdempotencyStore store, string key, IdempotencyClaim claim)
    {
        try
        {
            // A non-cancellable token so the cleanup happens even though the request itself was cancelled.
            await store.ReleaseAsync(key, claim.Token!, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>
    ///     Records a completed request. Never throws: a failure only costs a future duplicate its replay (it is
    ///     rejected instead), and is handed back for the caller to log.
    /// </summary>
    /// <returns>The store's failure, or <c>null</c> once the completion is recorded.</returns>
    public static async Task<Exception?> CompleteAsync(IIdempotencyStore store, string key, IdempotencyClaim claim, byte[]? result)
    {
        try
        {
            await store.CompleteAsync(key, claim.Token!, result, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static string? Fingerprint(IIdempotentRequest request, IRequestFingerprinter? fingerprinter)
    {
        if (request is IFingerprintedRequest fingerprinted)
        {
            var own = fingerprinted.Fingerprint;
            return string.IsNullOrEmpty(own) ? null : ScopedDigest(request.GetType(), own);
        }

        return fingerprinter is not null && fingerprinter.TryFingerprint(request, out var fingerprint) ? fingerprint : null;
    }

    // A request's own fingerprint is scoped by its type, as the generated ones are: two request types that render the
    // same string (a Transfer and a Refund, both "amount:100") under one key are a mismatch, not a replay of each other.
    // The type is named without assembly versions (Type.ToString, not the FullName of a generic type), which a runtime or
    // package upgrade would change; no type name holds a NUL, so the separator keeps the two parts apart. The store is
    // given the SHA-256 digest, the 64-hex-character form the generated fingerprints take, so any fingerprint of any
    // length fits the column a store sizes for it.
    private static string ScopedDigest(Type requestType, string fingerprint)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestType.ToString() + "\0" + fingerprint)));
}
