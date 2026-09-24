namespace CQRSharp;

/// <summary>
///     Published when a started query fails: a pre-handler, the handler or a post-handler threw, the query was
///     cancelled, or it ran out of time.
/// </summary>
/// <remarks>
///     After a <see cref="QueryInitiatedNotification{TResult}" />, exactly one of
///     <see cref="QueryCompletedNotification{TResult}" /> or <see cref="QueryFailedNotification{TResult}" /> is published.
///     It is delivered under no cancellation token, since the query's own may be the one that was cancelled; a subscriber
///     that fails is logged and never replaces the query's exception.
/// </remarks>
/// <typeparam name="TResult">The type of the result the query would have returned.</typeparam>
public sealed class QueryFailedNotification<TResult> : INotification
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="QueryFailedNotification{TResult}" /> class.
    /// </summary>
    /// <param name="query">The query that failed.</param>
    /// <param name="exception">The exception the query failed with, which its caller receives.</param>
    public QueryFailedNotification(IQuery<TResult> query, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(exception);
        Query = query;
        QueryName = query.GetType().Name;
        Exception = exception;
    }

    /// <summary>Gets the query that failed.</summary>
    public IQuery<TResult> Query { get; }

    /// <summary>Gets the name of the query's type.</summary>
    public string QueryName { get; }

    /// <summary>Gets the exception the query failed with.</summary>
    public Exception Exception { get; }
}
