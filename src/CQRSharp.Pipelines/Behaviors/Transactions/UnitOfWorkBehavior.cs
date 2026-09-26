using CQRSharp.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Pipelines;

/// <summary>
///     Runs a transactional request (<see cref="ITransactionalCommand" />, <see cref="ITransactionalQuery" />) in a
///     transaction of the scope's <see cref="IUnitOfWork" />: begun before the handler, committed with the request's
///     outbox notifications when it succeeds, rolled back (its notifications discarded) when it throws or, unless
///     <see cref="UnitOfWorkOptions.RollbackOnFailedResult" /> is <c>false</c>, returns a failed
///     <see cref="CommandResult" />. A read-only query (<see cref="ITransactionalQuery.IsReadOnly" />) is always rolled
///     back. A request that finds a transaction already active takes part in it, and whoever began it commits or rolls
///     it back.
/// </summary>
/// <typeparam name="TRequest">The type of the request.</typeparam>
/// <typeparam name="TResult">The type of the result.</typeparam>
/// <param name="logger">The behavior's logger.</param>
/// <param name="unitOfWork">The scope's unit of work.</param>
/// <param name="options">The unit-of-work options.</param>
/// <param name="services">The request's scoped services, from which the outbox services are resolved when a commit stores notifications.</param>
public sealed class UnitOfWorkBehavior<TRequest, TResult>(
    ILogger<UnitOfWorkBehavior<TRequest, TResult>> logger,
    IUnitOfWork unitOfWork,
    IOptions<UnitOfWorkOptions> options,
    IServiceProvider services)
    : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior where TRequest : IRequest
{
    /// <inheritdoc />
    public int PipelineExecutionPriority => CqrsPipelinePriorities.UnitOfWork;

    /// <inheritdoc />
    public async Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken)
    {
        if (!UnitOfWorkSupport.IsTransactional(request)) return await next(cancellationToken).ConfigureAwait(false);

        using var activity = PipelineTelemetry.StartActivity<TRequest>("UoW.Transaction");
        var unitOfWorkOptions = options.Value;

        var transaction = await UnitOfWorkTransaction.BeginAsync(
            unitOfWork, request, unitOfWorkOptions, services, logger, activity, typeof(TRequest).Name, cancellationToken).ConfigureAwait(false);
        if (transaction is null) return await next(cancellationToken).ConfigureAwait(false);

        TResult response;
        try
        {
            response = await next(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await transaction.RollBackAfterFailureAsync(ex, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (UnitOfWorkSupport.IsFailedResult(response, unitOfWorkOptions))
            await transaction.RollBackFailedResultAsync().ConfigureAwait(false);
        else if (!UnitOfWorkSupport.CommitsChanges(request))
            await transaction.RollBackReadOnlyAsync().ConfigureAwait(false);
        else
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return response;
    }
}
