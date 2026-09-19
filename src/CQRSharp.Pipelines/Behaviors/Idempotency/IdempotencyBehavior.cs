using CQRSharp.Core.Pipelines;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Pipelines.Behaviors.Idempotency;

/// <summary>
///     Enforces at-most-once processing for requests that implement <see cref="IIdempotentRequest" />. The request's
///     <see cref="IIdempotentRequest.IdempotencyKey" /> is claimed in the <see cref="IIdempotencyStore" /> before the
///     handler runs; a request whose key is already claimed is rejected with a <see cref="DuplicateRequestException" />.
/// </summary>
/// <remarks>
///     If the claimed request fails, the claim is released so a later attempt (including a resilience retry) can
///     re-process it. Requests that do not implement <see cref="IIdempotentRequest" /> pass through untouched.
/// </remarks>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResult">The result type.</typeparam>
public sealed class IdempotencyBehavior<TRequest, TResult>(
    ILogger<IdempotencyBehavior<TRequest, TResult>> logger,
    IIdempotencyStore store,
    IIdempotencyResultSerializer? resultSerializer = null) : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior, ICqrsIdempotencyBehaviorMarker
    where TRequest : IRequest
{
    /// <inheritdoc />
    public async Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken)
    {
        if (request is not IIdempotentRequest idempotent)
            return await next(cancellationToken).ConfigureAwait(false);

        var key = idempotent.IdempotencyKey;
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException(
                $"{typeof(TRequest).Name} implements {nameof(IIdempotentRequest)} but supplied an empty {nameof(IIdempotentRequest.IdempotencyKey)}.");

        var claim = await store.TryClaimAsync(key, cancellationToken).ConfigureAwait(false);
        switch (claim.Status)
        {
            case IdempotencyClaimStatus.Claimed:
                break;

            case IdempotencyClaimStatus.Completed when TryReplay(claim.StoredResult, out var replayed):
                // The whole point of an idempotency key: the caller retried a request that had already gone through
                // (a lost response, a client timeout), so it gets the original outcome, and the work is not repeated.
                logger.LogInformation("Replayed the stored result of {RequestName} for idempotency key {Key}.", typeof(TRequest).Name, key);
                return replayed;

            default:
                logger.LogInformation("Rejected duplicate request {RequestName} with idempotency key {Key}.", typeof(TRequest).Name, key);
                throw new DuplicateRequestException(key, claim.Status == IdempotencyClaimStatus.InProgress);
        }

        TResult result;
        try
        {
            result = await next(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The claimed request did not complete; release the claim (with a non-cancellable token so cleanup runs
            // even when the request was cancelled) so a later attempt or retry is not rejected as a duplicate.
            await store.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        // A command that reports failure without throwing did not complete either: keeping its claim would reject
        // the caller's legitimate retry as a duplicate for the whole retention window.
        if (result is CommandResult { IsSuccess: false })
        {
            await store.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false);
            return result;
        }

        // The work is done and must not be repeated, whatever happens to this bookkeeping write: never the request's
        // token, and a failure to store the result only costs a future duplicate its replay (it is rejected instead).
        try
        {
            await store.CompleteAsync(key, SerializeForReplay(result), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record the completed result of {RequestName} for idempotency key {Key}; a duplicate will be rejected rather than replayed.", typeof(TRequest).Name, key);
        }

        return result;
    }

    /// <inheritdoc />
    public int PipelineExecutionPriority => CqrsPipelinePriorities.Idempotency;

    // A plain CommandResult is stored as nothing at all: only a successful command ever completes, so its replay is
    // CommandResult.FromSuccess(). Anything that carries a value needs the optional serializer.
    private byte[]? SerializeForReplay(TResult result)
    {
        if (typeof(TResult) == typeof(CommandResult) || resultSerializer is null) return null;
        return resultSerializer.TrySerialize(result, out var payload) ? payload : null;
    }

    private bool TryReplay(byte[]? stored, out TResult replayed)
    {
        if (typeof(TResult) == typeof(CommandResult))
        {
            replayed = (TResult)(object)CommandResult.FromSuccess();
            return true;
        }

        replayed = default!;
        return stored is not null && resultSerializer is not null && resultSerializer.TryDeserialize(stored, out replayed);
    }
}
