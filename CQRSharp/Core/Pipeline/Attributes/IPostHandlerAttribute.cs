using CQRSharp.Interfaces.Markers.Request;

namespace CQRSharp.Core.Pipeline.Attributes
{
    /// <summary>
    /// Defines an interface for attributes that perform actions after an executable unit is handled.
    /// </summary>
    public interface IPostHandlerAttribute
    {
        /// <summary>
        /// Invoked after the handler has been executed.
        /// </summary>
        /// <param name="request">The executable unit that was handled.</param>
        /// <param name="serviceProvider">The service provider for resolving dependencies.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task OnAfterHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken);

        /// <summary>
        /// Determines the priority of the attribute. Lower values are executed first.
        /// </summary>
        int Priority { get; }
    }
}