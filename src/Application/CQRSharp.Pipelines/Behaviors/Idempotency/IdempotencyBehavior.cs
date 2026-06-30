using CQRSharp.Abstractions.Interfaces.Idempotency;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Models.Idempotency;
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
    IIdempotencyStore store) : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior, ICqrsIdempotencyBehaviorMarker
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

        var claimed = await store.TryClaimAsync(key, cancellationToken).ConfigureAwait(false);
        if (!claimed)
        {
            logger.LogInformation("Rejected duplicate request {RequestName} with idempotency key {Key}.", typeof(TRequest).Name, key);
            throw new DuplicateRequestException(key);
        }

        try
        {
            return await next(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The claimed request did not complete; release the claim (with a non-cancellable token so cleanup runs
            // even when the request was cancelled) so a later attempt or retry is not rejected as a duplicate.
            await store.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public int PipelineExecutionPriority => CqrsPipelinePriorities.Idempotency;
}