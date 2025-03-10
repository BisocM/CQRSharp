using CQRSharp.Interfaces.Markers.Query;
using CQRSharp.Interfaces.Notifications;

namespace CQRSharp.Core.Notifications.Types;

/// <summary>
///     This notification contains information about the completed query and its result data.
///     It is used to signal that a query has finished executing and to provide the query's outcome by the dispatcher,
///     but directly before any post-execution attribute methods are executed.
/// </summary>
/// <remarks>
///     Since <see cref="Result" /> is an <see cref="object" />, in order to use the data type that
///     the query is meant to return, you must cast it to the type determined when the query was fired.
/// </remarks>
/// <typeparam name="TResult">The type of the result expected from the query.</typeparam>
public sealed class QueryCompletedNotification<TResult> : INotification
{
    /// <summary>
    /// Represents a notification that indicates the completion of an asynchronously-queued query.
    /// </summary>
    /// <param name="query"></param>
    /// <param name="result"></param>
    public QueryCompletedNotification(IQuery<TResult> query, object? result)
    {
        Query = query;
        Result = result;
        QueryName = query.GetType().Name;
    }

    /// <summary>
    /// The query instance that has completed.
    /// </summary>
    public IQuery<TResult> Query { get; }

    /// <summary>
    ///     Gets the name of the executed query.
    /// </summary>
    public string QueryName { get; }

    /// <summary>
    ///     Gets the result obtained after executing the query.
    /// </summary>
    public object? Result { get; }
}