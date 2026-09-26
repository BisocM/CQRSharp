using System.Diagnostics;
using System.Runtime.ExceptionServices;
using CQRSharp.Core.Pipelines;
using CQRSharp.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Pipelines;

/// <summary>
///     Runs a transactional streaming request in a transaction of the scope's <see cref="IUnitOfWork" />, as
///     <see cref="UnitOfWorkBehavior{TRequest,TResult}" /> does a command or query: committed once the stream has been
///     enumerated to its end, rolled back when it faults or its consumer stops early.
/// </summary>
/// <typeparam name="TRequest">The type of the streaming request.</typeparam>
/// <typeparam name="TItem">The type of the stream's items.</typeparam>
/// <param name="logger">The behavior's logger.</param>
/// <param name="unitOfWork">The scope's unit of work.</param>
/// <param name="options">The unit-of-work options.</param>
/// <param name="services">The request's scoped services, from which the outbox services are resolved when a commit stores notifications.</param>
public sealed class StreamUnitOfWorkBehavior<TRequest, TItem>(
    ILogger<StreamUnitOfWorkBehavior<TRequest, TItem>> logger,
    IUnitOfWork unitOfWork,
    IOptions<UnitOfWorkOptions> options,
    IServiceProvider services)
    : IStreamPipelineBehavior<TRequest, TItem>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    /// <inheritdoc />
    public int PipelineExecutionPriority => CqrsPipelinePriorities.UnitOfWork;

    /// <inheritdoc />
    public IAsyncEnumerable<TItem> Handle(
        TRequest request,
        StreamHandlerDelegate<TItem> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(next);

        if (!UnitOfWorkSupport.IsTransactional(request)) return next(cancellationToken);

        return ExecuteAsync();

        // The activity is started inside the iterator: a "using" in Handle would stop it as soon as Handle returns,
        // before the stream is enumerated, leaving a zero-length span.
        async IAsyncEnumerable<TItem> ExecuteAsync()
        {
            using var activity = PipelineTelemetry.StartActivity<TRequest>("UoW.Transaction");

            var transaction = await UnitOfWorkTransaction.BeginAsync(
                unitOfWork, request, options.Value, services, logger, activity, typeof(TRequest).Name, cancellationToken).ConfigureAwait(false);
            if (transaction is null)
            {
                // Taking part in another request's transaction: nothing to settle here, but the stream is disposed the
                // way every layer disposes it, so a failing disposal never replaces the stream's own failure.
                Exception? participatingFailure = null;
                var participating = next(cancellationToken).GetAsyncEnumerator(cancellationToken);
                var participatingDisposed = false;
                try
                {
                    while (true)
                    {
                        bool moved;
                        try
                        {
                            // Each step resumes in the consumer's flow: the span is made current again, so what the rest
                            // of the pipeline starts during the step is parented to it.
                            if (activity is not null) Activity.Current = activity;
                            moved = await participating.MoveNextAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            participatingFailure = ex;
                            break;
                        }

                        if (!moved) break;
                        yield return participating.Current;
                    }

                    participatingDisposed = true;
                    participatingFailure = await DisposeAsync(participating, participatingFailure).ConfigureAwait(false);
                }
                finally
                {
                    // The consumer stopped enumerating early: the stream it wraps is disposed along with this one.
                    if (!participatingDisposed)
                        await participating.DisposeAsync().ConfigureAwait(false);
                }

                if (participatingFailure is not null)
                    ExceptionDispatchInfo.Capture(participatingFailure).Throw();
                yield break;
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
                        bool moved;
                        try
                        {
                            if (activity is not null) Activity.Current = activity;
                            moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                            break;
                        }

                        if (!moved)
                        {
                            reachedEnd = true;
                            break;
                        }

                        yield return enumerator.Current;
                    }

                    disposed = true;
                    failure = await DisposeAsync(enumerator, failure).ConfigureAwait(false);
                }
                finally
                {
                    // The consumer stopped enumerating early: the stream it wraps is disposed along with this one.
                    if (!disposed)
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                }

                // Decided only once the handler's enumerator is disposed: a stream whose disposal fails did not complete,
                // and is rolled back as the failure it is.
                completed = reachedEnd && failure is null;
            }
            finally
            {
                // Runs on a fault, and on an early disposal by the consumer (which never resumes past the loop above).
                if (!completed)
                {
                    if (failure is not null)
                        await transaction.RollBackAfterFailureAsync(failure, cancellationToken).ConfigureAwait(false);
                    else
                        await transaction.RollBackAbandonedAsync(UnitOfWorkSupport.CommitsChanges(request)).ConfigureAwait(false);
                }
            }

            if (failure is not null)
                ExceptionDispatchInfo.Capture(failure).Throw();

            if (UnitOfWorkSupport.CommitsChanges(request))
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            else
                await transaction.RollBackReadOnlyAsync().ConfigureAwait(false);
        }
    }

    // Disposes the wrapped stream once it ran to its end or failed; see StreamDisposal.
    private async ValueTask<Exception?> DisposeAsync(IAsyncEnumerator<TItem> enumerator, Exception? failure)
    {
        var (ended, suppressed) = await StreamDisposal.DisposeAsync(enumerator, failure).ConfigureAwait(false);
        if (suppressed is not null) UnitOfWorkTransaction.LogStreamDisposalFailed(logger, suppressed, typeof(TRequest).Name);
        return ended;
    }
}
