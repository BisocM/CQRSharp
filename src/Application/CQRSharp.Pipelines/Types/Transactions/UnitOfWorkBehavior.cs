using System.Data;
using System.Diagnostics;
using CQRSharp.Abstractions.Data.Attributes.Pipelines;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Data.Interfaces.Notifications;
using CQRSharp.Abstractions.Data.Interfaces.Outbox;
using CQRSharp.Abstractions.Data.Interfaces.Transactions;
using CQRSharp.Abstractions.Data.Models.Outbox;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines.Options;
using CQRSharp.Pipelines.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Pipelines.Types.Transactions;

/// <summary>
///     A pipeline behavior that wraps request handling in a database transaction.
///     It also orchestrates saving notifications to an outbox store if the outbox pattern is used.
/// </summary>
/// <typeparam name="TRequest">The type of the request.</typeparam>
/// <typeparam name="TResult">The type of the result.</typeparam>
[PipelinePriority(100)]
public sealed class UnitOfWorkBehavior<TRequest, TResult>(
    ILogger<UnitOfWorkBehavior<TRequest, TResult>> logger,
    IUnitOfWork unitOfWork,
    IOutbox outbox,
    IOptions<UnitOfWorkOptions> options,
    IOutboxStore? outboxStore = null,
    INotificationSerializer? serializer = null)
    : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
{
    /// <inheritdoc />
    public async Task<TResult> Handle(TRequest request, Func<CancellationToken, Task<TResult>> next, CancellationToken cancellationToken)
    {
        var isTransactional = request is ITransactionalRequest or ITransactionalQuery;
        if (!isTransactional) return await next(cancellationToken).ConfigureAwait(false);

        using var activity = PipelineTelemetry.StartActivity("UoW.Transaction", request);
        activity?.SetTag("cqrsharp.request_type", typeof(TRequest).Name);

        if (unitOfWork is IExplicitUnitOfWork explicitUow)
            return await HandleExplicitTransactionAsync(request, next, cancellationToken, activity, explicitUow).ConfigureAwait(false);

        return await HandleImplicitTransactionAsync(request, next, cancellationToken, activity).ConfigureAwait(false);
    }

    private async Task<TResult> HandleExplicitTransactionAsync(TRequest request, Func<CancellationToken, Task<TResult>> next, CancellationToken cancellationToken,
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

            await explicitUow.RollbackAsync(cancellationToken).ConfigureAwait(false);
            activity?.AddEvent(new ActivityEvent("Transaction Rolled Back"));

            throw;
        }
    }

    private async Task<TResult> HandleImplicitTransactionAsync(TRequest request, Func<CancellationToken, Task<TResult>> next, CancellationToken cancellationToken,
        Activity? activity)
    {
        logger.LogTrace("Beginning implicit transaction for {RequestName}", typeof(TRequest).Name);
        try
        {
            var response = await next(cancellationToken).ConfigureAwait(false);

            if (request is ITransactionalRequest)
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
            throw;
        }
    }

    private IsolationLevel GetIsolationLevel(TRequest request)
    {
        return request switch
        {
            ITransactionalRequest treq when treq.IsolationLevel != IsolationLevel.Unspecified => treq.IsolationLevel,
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

        var messages = notifications.Select(n => new OutboxMessage(
            Guid.NewGuid(),
            serializer.GetNotificationName(n.GetType()),
            serializer.Serialize(n),
            DateTime.UtcNow,
            OutboxMessageStatus.Pending,
            null,
            null
        ));

        await outboxStore.StoreAsync(messages, cancellationToken).ConfigureAwait(false);
    }
}
