using System.Runtime.ExceptionServices;
using CQRSharp.Core.Idempotency;
using CQRSharp.Core.Pipelines;
using CQRSharp.Persistence;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Pipelines;

/// <summary>
///     The streaming counterpart of <see cref="IdempotencyBehavior{TRequest,TResult}" />: enforces at-most-once
///     processing for a streaming request that implements <see cref="IIdempotentRequest" />. The key is claimed when
///     enumeration starts and kept only if the stream runs to completion; a stream that faults, is cancelled, or is
///     abandoned by its consumer releases the claim so the request can be retried.
/// </summary>
/// <typeparam name="TRequest">The type of the streaming request.</typeparam>
/// <typeparam name="TItem">The type of the streamed items.</typeparam>
public sealed class StreamIdempotencyBehavior<TRequest, TItem>(
    ILogger<StreamIdempotencyBehavior<TRequest, TItem>> logger,
    IIdempotencyStore store,
    IRequestFingerprinter? fingerprinter = null) : IStreamPipelineBehavior<TRequest, TItem>, IPrioritizedPipelineBehavior, ICqrsIdempotencyBehaviorMarker
    where TRequest : IRequest
{
    /// <inheritdoc />
    public int PipelineExecutionPriority => CqrsPipelinePriorities.Idempotency;

    /// <inheritdoc />
    public IAsyncEnumerable<TItem> Handle(TRequest request, StreamHandlerDelegate<TItem> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(next);

        return request is IIdempotentRequest idempotent
            ? Guarded(idempotent, next, cancellationToken)
            : next(cancellationToken);
    }

    // The token is the request's own (the executor already links it with the consumer's enumerator token), so it is
    // deliberately not an [EnumeratorCancellation] parameter.
#pragma warning disable CS8425
    private async IAsyncEnumerable<TItem> Guarded(
        IIdempotentRequest idempotent,
        StreamHandlerDelegate<TItem> next,
        CancellationToken cancellationToken)
#pragma warning restore CS8425
    {
        var key = IdempotencyClaims.KeyOf<TRequest>(idempotent);

        // A stream's items are not stored, so a completed stream cannot be replayed: any duplicate is rejected.
        var claim = await IdempotencyClaims.ClaimAsync(store, key, idempotent, fingerprinter, cancellationToken).ConfigureAwait(false);
        if (claim.Status == IdempotencyClaimStatus.PayloadMismatch)
        {
            IdempotencyLog.StreamMismatch(logger, typeof(TRequest).Name, key);
            throw new IdempotencyKeyMismatchException(key);
        }

        if (!claim.IsClaimed)
        {
            IdempotencyLog.StreamRejected(logger, typeof(TRequest).Name, key);
            throw new DuplicateRequestException(key, claim.Status == IdempotencyClaimStatus.InProgress);
        }

        var completed = false;
        Exception? failure = null;
        try
        {
            var reachedEnd = false;
            var enumerator = next(cancellationToken).GetAsyncEnumerator(cancellationToken);
            var disposed = false;
            try
            {
                while (true)
                {
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            reachedEnd = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        break;
                    }

                    yield return enumerator.Current;
                }

                disposed = true;
                var (ended, suppressed) = await StreamDisposal.DisposeAsync(enumerator, failure).ConfigureAwait(false);
                if (suppressed is not null) IdempotencyLog.StreamDisposalFailed(logger, suppressed, typeof(TRequest).Name);
                failure = ended;
            }
            finally
            {
                // The consumer stopped enumerating early: the stream it wraps is disposed along with this one.
                if (!disposed)
                    await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            // Only a stream that ran to its end and was disposed cleanly completed: one whose disposal fails is a
            // failure its caller sees, so its key is released for the retry rather than recorded as done.
            completed = reachedEnd && failure is null;
        }
        finally
        {
            // Runs on a fault and also when the consumer disposes the enumerator early: either way the request did not
            // complete. Bookkeeping never replaces the stream's own outcome: a failing store is logged.
            if (completed)
            {
                if (await IdempotencyClaims.CompleteAsync(store, key, claim, null).ConfigureAwait(false) is { } storeFailure)
                    IdempotencyLog.StreamCompletionNotStored(logger, storeFailure, typeof(TRequest).Name, key);
            }
            else if (await IdempotencyClaims.ReleaseAsync(store, key, claim).ConfigureAwait(false) is { } storeFailure)
            {
                IdempotencyLog.StreamReleaseFailed(logger, storeFailure, typeof(TRequest).Name, key);
            }
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
