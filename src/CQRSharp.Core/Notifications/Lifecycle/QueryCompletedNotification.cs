namespace CQRSharp;

/// <summary>
///     Published when a query completed: its handler returned and its post-handlers ran without throwing.
/// </summary>
/// <typeparam name="TResult">The type of the result the query returns.</typeparam>
public sealed class QueryCompletedNotification<TResult> : INotification
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="QueryCompletedNotification{TResult}" /> class.
    /// </summary>
    /// <param name="query">The query that completed.</param>
    /// <param name="result">The result the query's handler returned.</param>
    public QueryCompletedNotification(IQuery<TResult> query, TResult result)
    {
        ArgumentNullException.ThrowIfNull(query);
        Query = query;
        Result = result;
        QueryName = query.GetType().Name;
    }

    /// <summary>Gets the query that completed.</summary>
    public IQuery<TResult> Query { get; }

    /// <summary>Gets the name of the query's type.</summary>
    public string QueryName { get; }

    /// <summary>Gets the result the query's handler returned.</summary>
    public TResult Result { get; }
}
