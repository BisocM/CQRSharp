using CQRSharp.Interfaces.Markers.Query;
using CQRSharp.Interfaces.Notifications;

namespace CQRSharp.Core.Notifications.Types;

/// <summary>
///     Represents a notification that is published when a command is initiated.
///     This notification is published by the Dispatcher automatically
///     right before the execution of the command is initiated, before the execution of
///     pre-handling attributes.
/// </summary>
/// <typeparam name="TResult">The type of the result expected from the query.</typeparam>
public sealed class QueryInitiatedNotification<TResult>(IQuery<TResult> query) : INotification
{
    /// <summary>
    ///     Gets the name of the query that was initiated.
    /// </summary>
    public string QueryName { get; } = query.GetType().Name;
}