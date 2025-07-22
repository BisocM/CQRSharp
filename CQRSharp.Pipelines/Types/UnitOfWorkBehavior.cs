using CQRSharp.Core.Pipelines;
using CQRSharp.Abstractions.Data.Attributes.Pipelines;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Query;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Data.Interfaces.Transactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Pipelines.Types;

/// <summary>
/// Implements a pipeline behavior that automatically manages database transactions for commands.
/// This behavior intercepts requests that implement the <see cref="ITransactionalRequest"/> marker interface.
/// For such requests, it creates a new dependency injection scope, resolves an <see cref="IUnitOfWork"/>,
/// and executes the subsequent handlers within a transaction. The transaction is committed upon successful
/// execution or automatically rolled back if an exception occurs.
/// </summary>
/// <typeparam name="TRequest">The type of the request being handled.</typeparam>
/// <typeparam name="TResult">The result type of the request handler.</typeparam>
[PipelinePriority(100)]
public sealed class UnitOfWorkBehavior<TRequest, TResult>(
    ILogger<UnitOfWorkBehavior<TRequest, TResult>> logger,
    IServiceProvider serviceProvider)
    : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
{
    /// <inheritdoc />
    public async Task<TResult> Handle(TRequest request, Func<CancellationToken, Task<TResult>> next, CancellationToken cancellationToken)
    {
        bool isTransactional = request is ITransactionalRequest or ITransactionalQuery;
        if (!isTransactional)
        {
            return await next(cancellationToken).ConfigureAwait(false);
        }

        await using var scope = serviceProvider.CreateAsyncScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // Case 1: The UoW supports EXPLICIT control.
        if (uow is IExplicitUnitOfWork explicitUow)
        {
            var level = request is ITransactionalRequest treq ? treq.IsolationLevel 
                : (request as ITransactionalQuery<TResult>)!.IsolationLevel;
    
            await explicitUow.BeginTransactionAsync(level, cancellationToken);

            try
            {
                var response = await next(cancellationToken).ConfigureAwait(false);
                // If the pipeline succeeds, commit the transaction.
                await explicitUow.CommitAsync(cancellationToken).ConfigureAwait(false);
                return response;
            }
            catch (Exception)
            {
                // If anything fails, roll it back.
                await explicitUow.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw; // Re-throw the exception.
            }
        }

        // Case 2: IMPLICIT control (the default).
        logger.LogTrace("Beginning implicit transaction for {RequestName}", typeof(TRequest).Name);
        try
        {
            var response = await next(cancellationToken).ConfigureAwait(false);

            // Only commit for requests that can write data. Queries are read-only.
            if (request is not ITransactionalRequest) return response;
            
            logger.LogTrace("Committing implicit transaction for {RequestName}", typeof(TRequest).Name);
            await uow.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Implicit transaction failed for {RequestName}. Rolling back.", typeof(TRequest).Name);
            throw;
        }
    }
}