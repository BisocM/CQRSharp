using CQRSharp.Interfaces.Markers;

namespace CQRSharp.Core.Pipeline.Attributes
{
    /// <summary>
    /// Defines an interface for attributes that perform actions before an executable unit is handled.
    /// </summary>
    public interface IPreHandlerAttribute
    {
        /// <summary>
        /// Invoked before the handler is executed.
        /// </summary>
        /// <param name="request">The executable unit being handled.</param>
        /// <param name="serviceProvider">The service provider for resolving dependencies.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task OnBeforeHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken);

        /// <summary>
        /// Determines the priority of the attribute. Lower values are executed first.
        /// </summary>
        int Priority { get; }
    }
}