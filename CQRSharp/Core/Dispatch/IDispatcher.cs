using CQRSharp.Core.Options.Enums;
using CQRSharp.Interfaces.Markers.Command;
using CQRSharp.Interfaces.Markers.Query;

namespace CQRSharp.Core.Dispatch
{
    /// <summary>
    /// Defines a dispatcher interface for sending commands to their respective handlers.
    /// </summary>
    public interface IDispatcher
    {
        /// <summary>
        /// Sends a command without expecting a result.
        /// </summary>
        /// <param name="command">The command to send.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task ExecuteCommand(ICommand command, CancellationToken cancellationToken = default);

        /// <summary>
        /// Executes a query and returns a result.
        /// </summary>
        /// <typeparam name="TResult">The type of the result expected.</typeparam>
        /// <param name="query">The query to send.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A task that represents the asynchronous operation and contains the result.</returns>
        /// <remarks>
        /// Please pay attention to your application's run mode - synchronous or asynchronous.
        /// If your run mode is <see cref="RunMode.Async"/>, the query will be executed asynchronously, meaning that this method will always return a default value.
        /// In order to retrieve data from asynchronous queries, you must subscribe to the <see cref="IQuery{TResult}"/> result event.
        /// </remarks>
        Task<TResult?> ExecuteQuery<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default);
    }
}