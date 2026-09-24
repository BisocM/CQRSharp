using System.Diagnostics;
using CQRSharp.Core.Outbox;
using CQRSharp.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Pipelines;

/// <summary>
///     The lifecycle of one request's unit-of-work transaction, shared by the command/query and the streaming
///     unit-of-work behaviors so the two cannot drift: they differ only in how they run the rest of the pipeline.
/// </summary>
/// <remarks>
///     The transaction owns the notifications its request buffers from the moment it began (those of the request's
///     nested requests included): it stores them when it commits and discards them when it rolls back, and never
///     touches what an enclosing request buffered.
/// </remarks>
internal sealed partial class UnitOfWorkTransaction
{
    private static readonly ActivityEvent Committed = new("Transaction Committed");
    private static readonly ActivityEvent RolledBack = new("Transaction Rolled Back");

    private readonly IUnitOfWork _unitOfWork;
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;
    private readonly Activity? _activity;
    private readonly string _requestName;
    private readonly ScopedOutbox? _outbox;
    private readonly OutboxOwner? _owner;

    private UnitOfWorkTransaction(
        IUnitOfWork unitOfWork,
        IServiceProvider services,
        ILogger logger,
        Activity? activity,
        string requestName)
    {
        _unitOfWork = unitOfWork;
        _services = services;
        _logger = logger;
        _activity = activity;
        _requestName = requestName;

        // What the request buffers is attributed to the owner that is current while it runs (its own, set by the
        // executor): that is what this transaction commits or discards.
        _owner = OutboxOwner.Current;
        _outbox = _owner is null ? null : services.GetService<ScopedOutbox>();
    }

    /// <summary>
    ///     Begins the request's transaction; <c>null</c> when a transaction is already active in the scope, which the
    ///     request then takes part in without committing or rolling it back itself.
    /// </summary>
    public static async Task<UnitOfWorkTransaction?> BeginAsync(
        IUnitOfWork unitOfWork,
        IRequest request,
        UnitOfWorkOptions options,
        IServiceProvider services,
        ILogger logger,
        Activity? activity,
        string requestName,
        CancellationToken cancellationToken)
    {
        if (unitOfWork.HasActiveTransaction)
        {
            LogParticipating(logger, requestName);
            activity?.AddEvent(new ActivityEvent("Participating in existing transaction"));
            return null;
        }

        var level = UnitOfWorkSupport.GetIsolationLevel(request, options);
        activity?.SetTag(Core.Diagnostics.CqrsTelemetry.Tags.IsolationLevel, level.ToString());

        await unitOfWork.BeginTransactionAsync(level, cancellationToken).ConfigureAwait(false);
        activity?.AddEvent(new ActivityEvent("Transaction Started"));
        return new UnitOfWorkTransaction(unitOfWork, services, logger, activity, requestName);
    }

    /// <summary>
    ///     Commits the request's work with its notifications (see <see cref="OutboxCommit" />): a store that joins the
    ///     transaction gets them just before the commit, inside it; any other store right after the commit succeeded, so a
    ///     commit that fails publishes nothing. A failure before the commit completes rolls back and rethrows; a store
    ///     failure after it is logged and never undoes the committed work.
    /// </summary>
    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        OutboxCommit notifications;
        try
        {
            notifications = await OutboxCommit.PrepareAsync(_outbox, _owner, _services, cancellationToken).ConfigureAwait(false);
            await _unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Rolled back too, or the transaction stays open and every later transactional request in this scope silently
            // takes part in it and is never committed. The drained notifications go with the rolled-back work. The
            // failure itself propagates, so it is logged where it is handled, not here.
            LogCommitFailed(_logger, _requestName, ex.GetType().Name);
            _activity?.SetStatus(ActivityStatusCode.Error, "Transaction rolled back due to a commit failure.");
            await RollBackQuietlyAsync(discardNotifications: true).ConfigureAwait(false);
            throw;
        }

        _activity?.AddEvent(Committed);
        _activity?.SetStatus(ActivityStatusCode.Ok);

        // The work is committed and stands: notifications that could not be stored after the commit are lost, and the
        // log says which.
        if (await notifications.CompleteAsync().ConfigureAwait(false) is { } storeFailure)
        {
            var lost = notifications.Notifications;
            LogStoreAfterCommitFailed(_logger, storeFailure, _requestName, lost.Count, string.Join(", ", lost.Select(n => n.GetType().Name)));
            _activity?.AddEvent(new ActivityEvent("Outbox Store Failed"));
        }
    }

    /// <summary>
    ///     The request threw or its stream faulted: roll back and discard its notifications. The failure propagates, so
    ///     only the rollback is logged here; a rollback that fails is logged with its exception, so it never replaces the
    ///     failure the caller rethrows.
    /// </summary>
    /// <param name="failure">What the request threw.</param>
    /// <param name="cancellationToken">The request's token, which tells a caller that gave up from a failure.</param>
    public Task RollBackAfterFailureAsync(Exception failure, CancellationToken cancellationToken)
    {
        if (failure is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            LogCanceled(_logger, _requestName);
            _activity?.SetStatus(ActivityStatusCode.Error, "Transaction rolled back because the request was canceled.");
        }
        else
        {
            LogRollingBackAfterFailure(_logger, _requestName, failure.GetType().Name);
            _activity?.SetStatus(ActivityStatusCode.Error, "Transaction rolled back due to an exception.");
        }

        return RollBackQuietlyAsync(discardNotifications: true);
    }

    /// <summary>The consumer stopped enumerating the request's stream before its end: roll back and discard.</summary>
    /// <param name="commitsChanges">
    ///     Whether the stream would have committed its work; a read-only stream loses nothing by being rolled back.
    /// </param>
    public Task RollBackAbandonedAsync(bool commitsChanges)
    {
        if (commitsChanges)
            LogStreamAbandoned(_logger, _requestName);
        else
            LogReadOnlyStreamAbandoned(_logger, _requestName);

        _activity?.SetStatus(ActivityStatusCode.Error, "Transaction rolled back due to incomplete stream consumption.");
        return RollBackQuietlyAsync(discardNotifications: true);
    }

    /// <summary>
    ///     The handler returned a failed result: roll back and discard its notifications. There is no original error to
    ///     preserve, so a rollback that fails is the failure the caller sees (the transaction's state is unknown).
    /// </summary>
    public Task RollBackFailedResultAsync()
    {
        LogFailedResult(_logger, _requestName);
        _activity?.SetStatus(ActivityStatusCode.Error, "Transaction rolled back due to a failed result.");
        return RollBackAsync(discardNotifications: true);
    }

    /// <summary>
    ///     A read-only query held the transaction for a consistent read: roll it back. Its notifications stay buffered
    ///     for the request to settle, exactly as without a unit of work. A rollback that fails surfaces.
    /// </summary>
    public async Task RollBackReadOnlyAsync()
    {
        await RollBackAsync(discardNotifications: false).ConfigureAwait(false);
        _activity?.SetStatus(ActivityStatusCode.Ok);
    }

    // A rollback that fails propagates: it is then the request's failure, logged where that is handled.
    private async Task RollBackAsync(bool discardNotifications)
    {
        if (discardNotifications) DiscardNotifications();
        try
        {
            // Never the caller's token: it is typically what ended the request, and a rollback that is cancelled before
            // it starts leaves the transaction open for every later request in this scope.
            await _unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            _activity?.AddEvent(RolledBack);
        }
        catch
        {
            _activity?.SetStatus(ActivityStatusCode.Error, "Rollback failed.");
            throw;
        }
    }

    // The failure that caused the rollback is what surfaces, so a rollback that fails is swallowed: this log is the only
    // trace of it.
    private async Task RollBackQuietlyAsync(bool discardNotifications)
    {
        try
        {
            await RollBackAsync(discardNotifications).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogRollbackFailed(_logger, ex, _requestName);
        }
    }

    // The rolled-back work never happened, so neither did its notifications: dropped rather than left for a retry
    // attempt, or the next request in the scope, to store.
    private void DiscardNotifications()
    {
        if (_owner is not null) _outbox?.DiscardOwned(_owner);
    }

    [LoggerMessage(4200, LogLevel.Trace, "Participating in the existing transaction for {RequestName}.")]
    private static partial void LogParticipating(ILogger logger, string requestName);

    // The rollbacks are Information: a failure propagates and is reported, once, by whoever handles it (the logging
    // behavior, the host), and a failed result or an abandoned stream is the request's or its consumer's own outcome.
    // These record only what the unit of work did about it.
    [LoggerMessage(4201, LogLevel.Information, "{RequestName} failed with {ExceptionType}; rolling back its transaction.")]
    private static partial void LogRollingBackAfterFailure(ILogger logger, string requestName, string exceptionType);

    [LoggerMessage(4202, LogLevel.Information, "Streaming request {RequestName} was not read to its end; rolling back its transaction and the work it did.")]
    private static partial void LogStreamAbandoned(ILogger logger, string requestName);

    [LoggerMessage(4203, LogLevel.Information, "{RequestName} returned a failed result; rolling back its transaction.")]
    private static partial void LogFailedResult(ILogger logger, string requestName);

    [LoggerMessage(4204, LogLevel.Information, "Committing the transaction of {RequestName} failed with {ExceptionType}; rolling back.")]
    private static partial void LogCommitFailed(ILogger logger, string requestName, string exceptionType);

    // Error: the rollback's failure is swallowed so the one that caused it surfaces, and the transaction's state is
    // unknown; this is the only trace of it.
    [LoggerMessage(4205, LogLevel.Error, "Rolling back the transaction of {RequestName} failed; the failure that caused the rollback is the one reported.")]
    private static partial void LogRollbackFailed(ILogger logger, Exception exception, string requestName);

    [LoggerMessage(4206, LogLevel.Error, "{RequestName} committed, but storing its {Count} outbox notification(s) failed; they are lost: {NotificationNames}.")]
    private static partial void LogStoreAfterCommitFailed(ILogger logger, Exception exception, string requestName, int count, string notificationNames);

    // A caller that gave up is not a failure of the request.
    [LoggerMessage(4207, LogLevel.Debug, "{RequestName} was canceled by its caller; rolling back its transaction.")]
    private static partial void LogCanceled(ILogger logger, string requestName);

    // A read-only stream is rolled back when it ends anyway: its consumer stopping early changes nothing.
    [LoggerMessage(4208, LogLevel.Debug, "Read-only streaming request {RequestName} was not read to its end; rolling back its transaction.")]
    private static partial void LogReadOnlyStreamAbandoned(ILogger logger, string requestName);

    // Warning: the stream's own failure surfaces and is rolled back; this disposal failure is reported nowhere else.
    [LoggerMessage(4209, LogLevel.Warning,
        "Disposing the stream of {RequestName}, which had already failed, failed as well; the stream's own failure is what its consumer receives.")]
    internal static partial void LogStreamDisposalFailed(ILogger logger, Exception exception, string requestName);
}
