using System.Data;

namespace CQRSharp.Pipelines;

/// <summary>
///     The unit-of-work decisions about a request, shared by the unit-of-work behaviors and by the behaviors that must
///     agree with them about what a request's outcome means.
/// </summary>
internal static class UnitOfWorkSupport
{
    /// <summary>Whether the request runs inside a unit of work at all.</summary>
    public static bool IsTransactional(IRequest request) => request is ITransactionalCommand or ITransactionalQuery;

    /// <summary>
    ///     Whether the request's changes are saved at the end: a transactional command always is; a transactional query
    ///     only when it declares that it writes (<see cref="ITransactionalQuery.IsReadOnly" /> is <c>false</c>) —
    ///     otherwise it held the transaction for a consistent read and is rolled back.
    /// </summary>
    public static bool CommitsChanges(IRequest request)
        => request is ITransactionalCommand || request is ITransactionalQuery { IsReadOnly: false };

    /// <summary>
    ///     The isolation level to begin the request's transaction with: the request's own, else the configured default,
    ///     else <see cref="IsolationLevel.Unspecified" /> (the data store's default). A value that names no concrete level
    ///     (an unset property holds 0, which is no member of the enum) counts as unset rather than reaching the provider,
    ///     which would reject it.
    /// </summary>
    public static IsolationLevel GetIsolationLevel(IRequest request, UnitOfWorkOptions options)
    {
        var requested = request switch
        {
            ITransactionalCommand command => command.IsolationLevel,
            ITransactionalQuery query => query.IsolationLevel,
            _ => IsolationLevel.Unspecified
        };

        if (IsConcrete(requested)) return requested;
        return IsConcrete(options.DefaultIsolationLevel) ? options.DefaultIsolationLevel : IsolationLevel.Unspecified;
    }

    /// <summary>Whether <paramref name="level" /> names an isolation level a data store can begin a transaction with.</summary>
    public static bool IsConcrete(IsolationLevel level)
        => level is IsolationLevel.Chaos or IsolationLevel.ReadUncommitted or IsolationLevel.ReadCommitted
            or IsolationLevel.RepeatableRead or IsolationLevel.Serializable or IsolationLevel.Snapshot;

    /// <summary>Whether a returned result counts as a failure that rolls the unit of work back.</summary>
    public static bool IsFailedResult<TResult>(TResult response, UnitOfWorkOptions options)
        => options.RollbackOnFailedResult && response is CommandResult { IsSuccess: false };

    /// <summary>
    ///     Whether a failed result the request returns is committed with its unit of work rather than rolled back: its
    ///     work stands, so nothing that treats a failed result as "not done" (an idempotency claim) may act as if it were.
    /// </summary>
    public static bool CommitsFailedResult(IRequest request, UnitOfWorkOptions options)
        => !options.RollbackOnFailedResult && CommitsChanges(request);
}
