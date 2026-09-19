using System.Runtime.ExceptionServices;
using CQRSharp.Core.Pipelines;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Pipelines.Behaviors.Idempotency;

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
    IIdempotencyStore store) : IStreamPipelineBehavior<TRequest, TItem>, IPrioritizedPipelineBehavior
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
        var key = idempotent.IdempotencyKey;
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException(
                $"{typeof(TRequest).Name} implements {nameof(IIdempotentRequest)} but supplied an empty {nameof(IIdempotentRequest.IdempotencyKey)}.");

        // A stream's items are not stored, so a completed stream cannot be replayed: any duplicate is rejected.
        var claim = await store.TryClaimAsync(key, cancellationToken).ConfigureAwait(false);
        if (!claim.IsClaimed)
        {
            logger.LogInformation("Rejected duplicate streaming request {RequestName} with idempotency key {Key}.", typeof(TRequest).Name, key);
            throw new DuplicateRequestException(key, claim.Status == IdempotencyClaimStatus.InProgress);
        }

        var completed = false;
        Exception? failure = null;
        try
        {
            await using var enumerator = next(cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        completed = true;
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
        }
        finally
        {
            // Runs on a fault and also when the consumer disposes the enumerator early: either way the request did not
            // complete. A non-cancellable token so the cleanup happens even though the request itself was cancelled.
            if (completed)
                await store.CompleteAsync(key, null, CancellationToken.None).ConfigureAwait(false);
            else
                await store.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false);
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
