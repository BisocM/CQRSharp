using CQRSharp.Abstractions.Interfaces.Markers.Query;
using CQRSharp.Abstractions.Interfaces.Notifications;

namespace CQRSharp.Core.Notifications.Types;

/// <summary>
///     Published when a query's handler throws, signalling that the query did not complete successfully.
/// </summary>
/// <remarks>
///     Complements <see cref="QueryCompletedNotification{TResult}" />: after a
///     <see cref="QueryInitiatedNotification{TResult}" />, exactly one of the completed/failed notifications is
///     published for a given execution.
/// </remarks>
/// <typeparam name="TResult">The type of the result the query would have produced.</typeparam>
public sealed class QueryFailedNotification<TResult> : INotification
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="QueryFailedNotification{TResult}" /> class.
    /// </summary>
    /// <param name="query">The query whose handler threw.</param>
    /// <param name="exception">The exception thrown by the handler.</param>
    public QueryFailedNotification(IQuery<TResult> query, Exception exception)
    {
        Query = query;
        QueryName = query.GetType().Name;
        Exception = exception;
    }

    /// <summary>Gets the query whose handler threw.</summary>
    public IQuery<TResult> Query { get; }

    /// <summary>Gets the type name of the failed query.</summary>
    public string QueryName { get; }

    /// <summary>Gets the exception thrown by the handler.</summary>
    public Exception Exception { get; }
}