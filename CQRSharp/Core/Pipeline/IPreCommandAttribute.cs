using System;
using System.Threading;
using System.Threading.Tasks;

namespace CQRSharp.Core.Pipeline
{
    /// <summary>
    /// Defines an interface for attributes that perform actions before a command is handled.
    /// </summary>
    public interface IPreCommandAttribute
    {
        /// <summary>
        /// Invoked before the command handler is executed.
        /// </summary>
        /// <param name="command">The command being handled.</param>
        /// <param name="serviceProvider">The service provider for resolving dependencies.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task OnBeforeHandle(object command, IServiceProvider serviceProvider, CancellationToken cancellationToken);
    }
}