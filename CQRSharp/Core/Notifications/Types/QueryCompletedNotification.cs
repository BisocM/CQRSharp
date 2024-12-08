using CQRSharp.Interfaces.Markers;
using CQRSharp.Interfaces.Markers.Query;

namespace CQRSharp.Core.Notifications.Types
{
    /// <summary>
    /// This notification contains information about the completed query and its result data.
    /// It is used to signal that a command has finished executing and to provide the command's outcome by the dispatcher,
    /// but directly before any post-execution attribute methods are executed.
    /// </summary>
    /// <remarks>
    /// Since the <see cref="Result"/> is an <see cref="Object"/>, in order to appropriately use the data type that the query
    /// is meant to return, you will have to cast it into the type that you have pre-determined during the firing of the query.
    /// </remarks>
    /// <typeparam name="TResult">The type of the result expected from the query.</typeparam>
    /// <param name="query">The executed query associated with this notification.</param>
    /// <param name="result">The result obtained after executing the query.</param>
    public sealed class QueryCompletedNotification<TResult>(IQuery<TResult> query, object? result) : INotification
    {
        /// <summary>
        /// Gets the name of the executed query.
        /// </summary>
        public string QueryName { get; } = query.GetType().Name;

        /// <summary>
        /// Gets the result obtained after executing the query.
        /// </summary>
        /// <remarks>
        /// Since the result is of type <see cref="Object"/>, it needs to be cast to the specific data type expected by the query
        /// to utilize the result appropriately. The type must be predetermined by the user based on the expected outcome of the query.
        /// </remarks>
        public object? Result { get; } = result;
    }
}