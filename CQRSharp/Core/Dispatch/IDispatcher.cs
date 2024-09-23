using CQRSharp.Interfaces.Markers;
using System.Threading;
using System.Threading.Tasks;

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
        Task Send(ICommand command, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a command and expects a result.
        /// </summary>
        /// <typeparam name="TResult">The type of the result expected.</typeparam>
        /// <param name="command">The command to send.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A task that represents the asynchronous operation and contains the result.</returns>
        Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);
    }
}