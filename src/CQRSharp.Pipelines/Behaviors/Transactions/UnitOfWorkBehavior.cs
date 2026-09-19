using System.Data;
using System.Diagnostics;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Abstractions.Interfaces.Outbox;
using CQRSharp.Abstractions.Interfaces.Transactions;
using CQRSharp.Abstractions.Models.Commands;
using CQRSharp.Core.Background.Outbox;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines.Options;
using CQRSharp.Pipelines.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Pipelines.Behaviors.Transactions;

/// <summary>
///     A pipeline behavior that wraps request handling in a database transaction.
///     It also orchestrates saving notifications to an outbox store if the outbox pattern is used.
/// </summary>
/// <typeparam name="TRequest">The type of the request.</typeparam>
/// <typeparam name="TResult">The type of the result.</typeparam>
public sealed class UnitOfWorkBehavior<TRequest, TResult>(
    ILogger<UnitOfWorkBehavior<TRequest, TResult>> logger,
    IUnitOfWork unitOfWork,
    IOutbox outbox,
    IOptions<UnitOfWorkOptions> options,
    IOutboxStore? outboxStore = null,
    INotificationSerializer? serializer = null,
    TimeProvider? timeProvider = null)
    : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior where TRequest : IRequest
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken)
    {
        var isTransactional = request is ITransactionalCommand or ITransactionalQuery;
        if (!isTransactional) return await next(cancellationToken).ConfigureAwait(false);

        using var activity = PipelineTelemetry.StartActivity("UoW.Transaction", request);
        activity?.SetTag("cqrsharp.request_type", typeof(TRequest).Name);

        if (unitOfWork is IExplicitUnitOfWork explicitUow)
            return await HandleExplicitTransactionAsync(request, next, cancellationToken, activity, explicitUow).ConfigureAwait(false);

        return await HandleImplicitTransactionAsync(request, next, cancellationToken, activity).ConfigureAwait(false);
    }

    public int PipelineExecutionPriority => CqrsPipelinePriorities.UnitOfWork;

    private async Task<TResult> HandleExplicitTransactionAsync(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken,
        Activity? activity, IExplicitUnitOfWork explicitUow)
    {
        if (explicitUow.HasActiveTransaction)
        {
            logger.LogTrace("Participating in existing transaction for {RequestName}", typeof(TRequest).Name);
            activity?.AddEvent(new ActivityEvent("Participating in existing transaction"));
            return await next(cancellationToken).ConfigureAwait(false);
        }

        var level = GetIsolationLevel(request);
        activity?.SetTag("db.isolation_level", level.ToString());

        await explicitUow.BeginTransactionAsync(level, cancellationToken);
        activity?.AddEvent(new ActivityEvent("Transaction Started"));

        try
        {
            var response = await next(cancellationToken).ConfigureAwait(false);

            if (IsFailedResult(response))
            {
                // The handler reported failure without throwing: same outcome for the transaction, no exception.
                logger.LogWarning("{RequestName} returned a failed result. Rolling back.", typeof(TRequest).Name);
                activity?.SetStatus(ActivityStatusCode.Error, "Transaction rolled back due to a failed result.");
                outbox.Drain();
                await explicitUow.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                activity?.AddEvent(new ActivityEvent("Transaction Rolled Back"));
                return response;
            }

            await SaveNotificationsFromOutboxAsync(cancellationToken).ConfigureAwait(false);

            await explicitUow.CommitAsync(cancellationToken).ConfigureAwait(false);
            activity?.AddEvent(new ActivityEvent("Transaction Committed"));
            activity?.SetStatus(ActivityStatusCode.Ok);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Transaction failed for {RequestName}. Rolling back.", typeof(TRequest).Name);
            activity?.SetStatus(ActivityStatusCode.Error, "Transaction rolled back due to an exception.");

            // The rolled-back work never happened, so neither did its notifications: drop them rather than leave them
            // in the scoped outbox for a retry attempt (or the next command in this scope) to persist.
            outbox.Drain();

            try
            {
                // Never the caller's token: it is typically what caused the failure, and a rollback that is cancelled
                // before it starts leaves the transaction open for every later request in this scope.
                await explicitUow.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                activity?.AddEvent(new ActivityEvent("Transaction Rolled Back"));
            }
            catch (Exception rollbackEx)
            {
                // A failing rollback must not mask the original error that triggered it; log and rethrow the original.
                logger.LogError(rollbackEx, "Rollback failed for {RequestName} after a transaction error.", typeof(TRequest).Name);
            }

            throw;
        }
    }

    private async Task<TResult> HandleImplicitTransactionAsync(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken,
        Activity? activity)
    {
        logger.LogTrace("Beginning implicit transaction for {RequestName}", typeof(TRequest).Name);
        try
        {
            var response = await next(cancellationToken).ConfigureAwait(false);

            if (IsFailedResult(response))
            {
                // Nothing is saved, so the implicit unit of work is simply abandoned along with its notifications.
                logger.LogWarning("{RequestName} returned a failed result. Its changes are not saved.", typeof(TRequest).Name);
                activity?.SetStatus(ActivityStatusCode.Error, "Implicit transaction abandoned due to a failed result.");
                outbox.Drain();
                return response;
            }

            if (request is ITransactionalCommand)
            {
                await SaveNotificationsFromOutboxAsync(cancellationToken).ConfigureAwait(false);

                logger.LogTrace("Committing implicit transaction for {RequestName}", typeof(TRequest).Name);
                await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                activity?.AddEvent(new ActivityEvent("Implicit Transaction Committed"));
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Implicit transaction failed for {RequestName}. The operation will be rolled back.", typeof(TRequest).Name);
            activity?.SetStatus(ActivityStatusCode.Error, "Implicit transaction failed.");
            outbox.Drain();
            throw;
        }
    }

    private bool IsFailedResult(TResult response)
        => options.Value.RollbackOnFailedResult && response is CommandResult { IsSuccess: false };

    private IsolationLevel GetIsolationLevel(TRequest request)
    {
        return request switch
        {
            ITransactionalCommand treq when treq.IsolationLevel != IsolationLevel.Unspecified => treq.IsolationLevel,
            ITransactionalQuery tquery when tquery.IsolationLevel != IsolationLevel.Unspecified => tquery.IsolationLevel,
            _ => options.Value.DefaultIsolationLevel
        };
    }

    private async Task SaveNotificationsFromOutboxAsync(CancellationToken cancellationToken)
    {
        var notifications = outbox.Drain();
        if (notifications.Count == 0) return;

        if (outboxStore is null || serializer is null)
            throw new InvalidOperationException("IOutbox is registered, but IOutboxStore or INotificationSerializer are missing. Please check your DI configuration.");

        var messages = OutboxMessageFactory.Create(notifications, serializer, _timeProvider);
        await outboxStore.StoreAsync(messages, cancellationToken).ConfigureAwait(false);
    }
}