using System.Data;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Abstractions.Interfaces.Outbox;
using CQRSharp.Abstractions.Interfaces.Transactions;
using CQRSharp.Core.Background.Outbox;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines.Options;
using CQRSharp.Pipelines.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Pipelines.Behaviors.Transactions;

/// <summary>
///     Wraps streaming request execution in a Unit of Work transaction when the request is transactional.
/// </summary>
public sealed class StreamUnitOfWorkBehavior<TRequest, TItem>(
    ILogger<StreamUnitOfWorkBehavior<TRequest, TItem>> logger,
    IUnitOfWork unitOfWork,
    IOutbox outbox,
    IOptions<UnitOfWorkOptions> options,
    IOutboxStore? outboxStore = null,
    INotificationSerializer? serializer = null,
    TimeProvider? timeProvider = null)
    : IStreamPipelineBehavior<TRequest, TItem>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public int PipelineExecutionPriority => CqrsPipelinePriorities.UnitOfWork;

    public IAsyncEnumerable<TItem> Handle(
        TRequest request,
        StreamHandlerDelegate<TItem> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(next);

        var isTransactional = request is ITransactionalCommand or ITransactionalQuery;
        if (!isTransactional) return next(cancellationToken);

        // The activity is started inside each iterator: a "using" here would stop it as soon as this (non-iterator)
        // method returns, before the stream is enumerated, leaving a zero-length span.
        if (unitOfWork is IExplicitUnitOfWork explicitUow)
            return HandleExplicitTransaction(request, next, cancellationToken, explicitUow);

        return HandleImplicitTransaction(request, next, cancellationToken);
    }

    private static Activity? StartTransactionActivity(TRequest request)
    {
        var activity = PipelineTelemetry.StartActivity("UoW.Transaction", request);
        activity?.SetTag("cqrsharp.request_type", typeof(TRequest).Name);
        return activity;
    }

    private IAsyncEnumerable<TItem> HandleExplicitTransaction(
        TRequest request,
        StreamHandlerDelegate<TItem> next,
        CancellationToken cancellationToken,
        IExplicitUnitOfWork explicitUow)
    {
        return ExecuteAsync();

        async IAsyncEnumerable<TItem> ExecuteAsync()
        {
            using var activity = StartTransactionActivity(request);

            if (explicitUow.HasActiveTransaction)
            {
                logger.LogTrace("Participating in existing transaction for {RequestName}", typeof(TRequest).Name);
                activity?.AddEvent(new ActivityEvent("Participating in existing transaction"));

                await foreach (var item in next(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                    yield return item;

                yield break;
            }

            var level = GetIsolationLevel(request);
            activity?.SetTag("db.isolation_level", level.ToString());

            await explicitUow.BeginTransactionAsync(level, cancellationToken).ConfigureAwait(false);
            activity?.AddEvent(new ActivityEvent("Transaction Started"));

            var completed = false;
            Exception? failure = null;

            try
            {
                await using (var enumerator = next(cancellationToken).GetAsyncEnumerator(cancellationToken))
                {
                    while (true)
                    {
                        bool moved;
                        try
                        {
                            moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                            break;
                        }

                        if (!moved)
                        {
                            completed = true;
                            break;
                        }

                        yield return enumerator.Current;
                    }
                }
            }
            finally
            {
                if (!completed)
                {
                    if (failure is not null)
                    {
                        logger.LogError(failure, "Transaction failed for {RequestName}. Rolling back.", typeof(TRequest).Name);
                        activity?.SetStatus(ActivityStatusCode.Error, "Transaction rolled back due to an exception.");
                    }
                    else
                    {
                        logger.LogWarning(
                            "Streaming request {RequestName} did not complete; rolling back transaction.",
                            typeof(TRequest).Name);
                        activity?.SetStatus(ActivityStatusCode.Error, "Transaction rolled back due to incomplete stream consumption.");
                    }

                    await RollbackAsync(explicitUow, activity).ConfigureAwait(false);
                }
            }

            if (!completed)
            {
                if (failure is not null)
                    ExceptionDispatchInfo.Capture(failure).Throw();

                yield break;
            }

            // A failed outbox save or commit must roll back too; otherwise the transaction stays open and every later
            // transactional request in this scope silently "participates" in it and is never committed.
            try
            {
                await SaveNotificationsFromOutboxAsync(cancellationToken).ConfigureAwait(false);
                await explicitUow.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception commitEx)
            {
                logger.LogError(commitEx, "Commit failed for {RequestName}. Rolling back.", typeof(TRequest).Name);
                activity?.SetStatus(ActivityStatusCode.Error, "Transaction rolled back due to a commit failure.");
                await RollbackAsync(explicitUow, activity).ConfigureAwait(false);
                throw;
            }

            activity?.AddEvent(new ActivityEvent("Transaction Committed"));
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
    }

    private async Task RollbackAsync(IExplicitUnitOfWork explicitUow, Activity? activity)
    {
        // The rolled-back work never happened, so neither did its notifications.
        outbox.Drain();

        try
        {
            // Never the caller's token: it is typically what ended the stream, and a rollback that is cancelled before
            // it starts leaves the transaction open for every later request in this scope.
            await explicitUow.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            activity?.AddEvent(new ActivityEvent("Transaction Rolled Back"));
        }
        catch (Exception rollbackEx)
        {
            // A failing rollback must not mask the original failure (rethrown by the caller).
            logger.LogError(rollbackEx, "Rollback failed for {RequestName} after a streaming transaction error.",
                typeof(TRequest).Name);
        }
    }

    private IAsyncEnumerable<TItem> HandleImplicitTransaction(
        TRequest request,
        StreamHandlerDelegate<TItem> next,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync();

        async IAsyncEnumerable<TItem> ExecuteAsync()
        {
            using var activity = StartTransactionActivity(request);

            logger.LogTrace("Beginning implicit transaction for {RequestName}", typeof(TRequest).Name);

            Exception? failure = null;
            var completed = false;

            await using (var enumerator = next(cancellationToken).GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        break;
                    }

                    if (!moved)
                    {
                        completed = true;
                        break;
                    }

                    yield return enumerator.Current;
                }
            }

            if (failure is not null)
            {
                logger.LogError(failure, "Implicit transaction failed for {RequestName}. The operation will be rolled back.", typeof(TRequest).Name);
                activity?.SetStatus(ActivityStatusCode.Error, "Implicit transaction failed.");
                outbox.Drain();
                ExceptionDispatchInfo.Capture(failure).Throw();
                yield break;
            }

            if (!completed) yield break;

            if (request is ITransactionalCommand)
            {
                await SaveNotificationsFromOutboxAsync(cancellationToken).ConfigureAwait(false);

                logger.LogTrace("Committing implicit transaction for {RequestName}", typeof(TRequest).Name);
                await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                activity?.AddEvent(new ActivityEvent("Implicit Transaction Committed"));
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
        }
    }

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
            throw new InvalidOperationException(
                "IOutbox is registered, but IOutboxStore or INotificationSerializer are missing. Please check your DI configuration.");

        var messages = OutboxMessageFactory.Create(notifications, serializer, _timeProvider);
        await outboxStore.StoreAsync(messages, cancellationToken).ConfigureAwait(false);
    }
}