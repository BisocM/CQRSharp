using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using CQRSharp.Core.Idempotency;
using CQRSharp.Core.Pipelines;
using CQRSharp.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Pipelines;

/// <summary>
///     Enforces at-most-once processing for requests that implement <see cref="IIdempotentRequest" />. The request's
///     <see cref="IIdempotentRequest.IdempotencyKey" /> is claimed in the <see cref="IIdempotencyStore" /> before the
///     handler runs; a request whose key is already claimed is rejected with a <see cref="DuplicateRequestException" />,
///     or answered with the original result when that completed and can be replayed.
/// </summary>
/// <remarks>
///     <para>
///         The claim carries the request's payload <b>fingerprint</b> — the one the request computes itself
///         (<see cref="IFingerprintedRequest" />) or the one the source generator derives from its properties, scoped by
///         the request's type either way — so a key reused for a <em>different</em> request, of the same type or another,
///         is rejected with <see cref="IdempotencyKeyMismatchException" /> rather than answered with another request's
///         result.
///     </para>
///     <para>
///         If the claimed request fails — it throws, or returns a failed <see cref="CommandResult" /> — the claim is
///         released so a later attempt (including a resilience retry) can re-process it. The one exception is a failed
///         result its unit of work commits (<see cref="UnitOfWorkOptions.RollbackOnFailedResult" /> set to <c>false</c>):
///         that work stands, so the claim completes and a duplicate gets the same failure back. Requests that do not
///         implement <see cref="IIdempotentRequest" /> pass through untouched.
///     </para>
/// </remarks>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResult">The result type.</typeparam>
/// <param name="logger">The behavior's logger.</param>
/// <param name="store">The store that holds the claims.</param>
/// <param name="unitOfWorkOptions">The unit-of-work options, which decide whether a failed result is committed.</param>
/// <param name="resultSerializer">Stores and replays results that carry a value; without it such a duplicate is rejected.</param>
/// <param name="fingerprinter">Derives a request's payload fingerprint when the request does not compute its own.</param>
public sealed class IdempotencyBehavior<TRequest, TResult>(
    ILogger<IdempotencyBehavior<TRequest, TResult>> logger,
    IIdempotencyStore store,
    IOptions<UnitOfWorkOptions> unitOfWorkOptions,
    IIdempotencyResultSerializer? resultSerializer = null,
    IRequestFingerprinter? fingerprinter = null) : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior, ICqrsIdempotencyBehaviorMarker
    where TRequest : IRequest
{
    // The serializers this closed behavior has reported as unable to store its result type. Keyed by the serializer
    // instance, a container singleton, so two hosts in one process each report their own; weakly, so a disposed host's
    // serializer is not kept alive by it.
    private static readonly ConditionalWeakTable<IIdempotencyResultSerializer, object> ReplayUnavailableReported = new();

    /// <inheritdoc />
    public async Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken)
    {
        if (request is not IIdempotentRequest idempotent)
            return await next(cancellationToken).ConfigureAwait(false);

        var key = IdempotencyClaims.KeyOf<TRequest>(idempotent);
        var claim = await IdempotencyClaims.ClaimAsync(store, key, idempotent, fingerprinter, cancellationToken).ConfigureAwait(false);
        switch (claim.Status)
        {
            case IdempotencyClaimStatus.Claimed:
                break;

            case IdempotencyClaimStatus.Completed when TryReplay(claim.StoredResult, out var replayed):
                // The whole point of an idempotency key: the caller retried a request that had already gone through
                // (a lost response, a client timeout), so it gets the original outcome, and the work is not repeated.
                IdempotencyLog.Replayed(logger, typeof(TRequest).Name, key);
                return replayed;

            case IdempotencyClaimStatus.PayloadMismatch:
                // The same key for a different request: replaying the original would answer the wrong question, and
                // running this one would break the key's promise. The client has to pick a new key.
                IdempotencyLog.Mismatch(logger, typeof(TRequest).Name, key);
                throw new IdempotencyKeyMismatchException(key);

            default:
                IdempotencyLog.Rejected(logger, typeof(TRequest).Name, key);
                throw new DuplicateRequestException(key, claim.Status == IdempotencyClaimStatus.InProgress);
        }

        TResult result;
        try
        {
            result = await next(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The claimed request did not complete; release the claim so a later attempt or retry is not rejected as
            // a duplicate. The request's own failure is what the caller sees, whatever the store does.
            await ReleaseAsync(key, claim).ConfigureAwait(false);
            throw;
        }

        // A command that reports failure without throwing did not complete either: keeping its claim would reject
        // the caller's legitimate retry as a duplicate for the whole retention window. Unless its unit of work
        // committed the failure: that work stands, and running it again is what the key exists to prevent.
        if (result is CommandResult { IsSuccess: false } && !UnitOfWorkSupport.CommitsFailedResult(request, unitOfWorkOptions.Value))
        {
            await ReleaseAsync(key, claim).ConfigureAwait(false);
            return result;
        }

        // The work is done and must not be repeated, whatever happens to this bookkeeping write - rendering the result
        // for replay included: a result that cannot be rendered completes the claim without one (a duplicate is then
        // rejected rather than replayed), and the request's own outcome still stands.
        byte[]? replay;
        try
        {
            replay = SerializeForReplay(result);
        }
        catch (Exception ex)
        {
            IdempotencyLog.CompletionNotStored(logger, ex, typeof(TRequest).Name, key);
            replay = null;
        }

        if (await IdempotencyClaims.CompleteAsync(store, key, claim, replay).ConfigureAwait(false) is { } storeFailure)
            IdempotencyLog.CompletionNotStored(logger, storeFailure, typeof(TRequest).Name, key);
        return result;
    }

    /// <inheritdoc />
    public int PipelineExecutionPriority => CqrsPipelinePriorities.Idempotency;

    private async Task ReleaseAsync(string key, IdempotencyClaim claim)
    {
        if (await IdempotencyClaims.ReleaseAsync(store, key, claim).ConfigureAwait(false) is { } storeFailure)
            IdempotencyLog.ReleaseFailed(logger, storeFailure, typeof(TRequest).Name, key);
    }

    // A successful plain CommandResult is stored as nothing at all, and replays as CommandResult.FromSuccess(); a
    // committed failure is stored as its failure record. Anything that carries a value needs the optional serializer.
    private byte[]? SerializeForReplay(TResult result)
    {
        if (typeof(TResult) == typeof(CommandResult))
            return result is CommandResult { IsSuccess: false } failure ? FailedCommandResultCodec.Serialize(failure) : null;

        if (resultSerializer is null) return null;
        if (resultSerializer.TrySerialize(result, out var payload)) return payload;

        // Once per request type and serializer: a result type the configured serializer cannot store (one missing from
        // the JsonSerializerContext, say) otherwise turns every duplicate into a rejection with no sign of why.
        if (ReplayUnavailableReported.TryAdd(resultSerializer, resultSerializer))
            IdempotencyLog.ReplayUnavailable(logger, typeof(TRequest).Name, resultSerializer.GetType().Name);
        return null;
    }

    private bool TryReplay(byte[]? stored, [MaybeNullWhen(false)] out TResult replayed)
    {
        if (typeof(TResult) == typeof(CommandResult))
        {
            if (stored is null)
            {
                replayed = (TResult)(object)CommandResult.FromSuccess();
                return true;
            }

            // A record that cannot be read is rejected as a duplicate rather than replayed as something it is not.
            var readable = FailedCommandResultCodec.TryDeserialize(stored, out var failure);
            replayed = readable ? (TResult)(object)failure : default!;
            return readable;
        }

        replayed = default;
        return stored is not null && resultSerializer is not null && resultSerializer.TryDeserialize(stored, out replayed);
    }
}
