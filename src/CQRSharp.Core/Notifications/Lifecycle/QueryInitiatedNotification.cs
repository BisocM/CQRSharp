namespace CQRSharp;

/// <summary>
///     Published when the executor starts a query, before its pre-handlers run.
/// </summary>
/// <remarks>
///     Exactly one terminal notification follows it: <see cref="QueryCompletedNotification{TResult}" /> when the query
///     succeeds, or <see cref="QueryFailedNotification{TResult}" /> when it fails.
/// </remarks>
/// <typeparam name="TResult">The type of the result the query returns.</typeparam>
public sealed class QueryInitiatedNotification<TResult> : INotification
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="QueryInitiatedNotification{TResult}" /> class.
    /// </summary>
    /// <param name="query">The query being started.</param>
    public QueryInitiatedNotification(IQuery<TResult> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        Query = query;
        QueryName = query.GetType().Name;
    }

    /// <summary>Gets the query being started.</summary>
    public IQuery<TResult> Query { get; }

    /// <summary>Gets the name of the query's type.</summary>
    public string QueryName { get; }
}
