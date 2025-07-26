using CQRSharp.Abstractions.Data.Interfaces.Markers.Query;
using CQRSharp.Abstractions.Data.Interfaces.Notifications;

namespace CQRSharp.Core.Notifications.Types;

/// <summary>
///     Represents a notification that is published when a query is initiated.
///     This notification is published by the RequestDispatcher automatically
///     right before the execution of the query is initiated, before pre-handle attributes.
/// </summary>
/// <typeparam name="TResult">The type of the result expected from the query.</typeparam>
public sealed class QueryInitiatedNotification<TResult> : INotification
{
    /// <summary>
    ///     Represents a notification that is published when a query is initiated.
    ///     This notification is triggered by the RequestDispatcher automatically before the execution of the query begins
    ///     and prior to processing any pre-handle attributes.
    /// </summary>
    public QueryInitiatedNotification(IQuery<TResult> query)
    {
        Query = query;
        QueryName = query.GetType().Name;
    }

    /// <summary>
    ///     Gets the query that was initiated.
    /// </summary>
    public IQuery<TResult> Query { get; }

    /// <summary>
    ///     Gets the name of the query that was initiated.
    /// </summary>
    public string QueryName { get; }
}