using CQRSharp.Interfaces.Markers;

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
        Task<TResult> ExecuteQuery<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default);
    }
}