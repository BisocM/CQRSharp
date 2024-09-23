using System;
using System.Threading;
using System.Threading.Tasks;

namespace CQRSharp.Core.Pipeline
{
    /// <summary>
    /// Defines an interface for attributes that perform actions after a command is handled.
    /// </summary>
    public interface IPostCommandAttribute
    {
        /// <summary>
        /// Invoked after the command handler has been executed.
        /// </summary>
        /// <param name="command">The command that was handled.</param>
        /// <param name="serviceProvider">The service provider for resolving dependencies.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task OnAfterHandle(object command, IServiceProvider serviceProvider, CancellationToken cancellationToken);
    }
}