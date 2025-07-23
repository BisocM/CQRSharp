using System.Data;
using System.Diagnostics;
using CQRSharp.Abstractions.Data.Attributes.Pipelines;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines.Options;
using CQRSharp.Pipelines.Telemetry;
using CQRSharp.Pipelines.Types.Transactions.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Pipelines.Types.Transactions;

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
    IServiceProvider serviceProvider,
    IOptions<UnitOfWorkOptions> options)
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
        
        // Start a new Activity for the transaction to enable distributed tracing.
        using var activity = PipelineTelemetry.StartActivity("UoW.Transaction", request);
        activity?.SetTag("cqrsharp.request_type", typeof(TRequest).Name);

        await using var scope = serviceProvider.CreateAsyncScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        if (uow is IExplicitUnitOfWork explicitUow)
        {
            // If we're already in a transaction, just participate without creating a new one.
            if (explicitUow.HasActiveTransaction)
            {
                logger.LogTrace("Participating in existing transaction for {RequestName}", typeof(TRequest).Name);
                activity?.AddEvent(new ActivityEvent("Participating in existing transaction"));
                return await next(cancellationToken).ConfigureAwait(false);
            }

            var level = request switch
            {
                ITransactionalRequest treq when treq.IsolationLevel != IsolationLevel.Unspecified => treq.IsolationLevel,
                ITransactionalQuery tquery when tquery.IsolationLevel != IsolationLevel.Unspecified => tquery.IsolationLevel,
                _ => options.Value.DefaultIsolationLevel
            };

            activity?.SetTag("db.isolation_level", level.ToString());

            await explicitUow.BeginTransactionAsync(level, cancellationToken);
            activity?.AddEvent(new ActivityEvent("Transaction Started"));

            try
            {
                var response = await next(cancellationToken).ConfigureAwait(false);

                await explicitUow.CommitAsync(cancellationToken).ConfigureAwait(false);
                activity?.AddEvent(new ActivityEvent("Transaction Committed"));
                activity?.SetStatus(ActivityStatusCode.Ok);
                return response;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Transaction failed for {RequestName}. Rolling back.", typeof(TRequest).Name);
                activity?.SetStatus(ActivityStatusCode.Error, "Transaction rolled back due to an exception.");
                
                // Attempt to roll back the transaction.
                await explicitUow.RollbackAsync(cancellationToken).ConfigureAwait(false);
                activity?.AddEvent(new ActivityEvent("Transaction Rolled Back"));
                
                throw; // Re-throw the exception to allow outer pipelines (like Resilience) to handle it.
            }
        }
        
        // Fallback for implicit transactions (e.g., simple DbContext.SaveChangesAsync pattern)
        logger.LogTrace("Beginning implicit transaction for {RequestName}", typeof(TRequest).Name);
        try
        {
            var response = await next(cancellationToken).ConfigureAwait(false);

            // For implicit transactions, only ITransactionalRequest triggers a save. Queries are assumed to be reads.
            if (request is ITransactionalRequest)
            {
                logger.LogTrace("Committing implicit transaction for {RequestName}", typeof(TRequest).Name);
                await uow.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                activity?.AddEvent(new ActivityEvent("Implicit Transaction Committed"));
            }
            
            activity?.SetStatus(ActivityStatusCode.Ok);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Implicit transaction failed for {RequestName}. The operation will be rolled back.", typeof(TRequest).Name);
            activity?.SetStatus(ActivityStatusCode.Error, "Implicit transaction failed.");
            throw;
        }
    }
}