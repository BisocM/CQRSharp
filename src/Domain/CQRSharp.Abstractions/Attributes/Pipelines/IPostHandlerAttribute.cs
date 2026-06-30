using CQRSharp.Abstractions.Interfaces.Markers.Request;

namespace CQRSharp.Abstractions.Attributes.Pipelines;

/// <summary>
///     Defines an interface for attributes that perform actions after an executable unit is handled.
/// </summary>
public interface IPostHandlerAttribute
{
    /// <summary>
    ///     Determines the priority of the attribute. Lower values are executed first.
    /// </summary>
    int PostHandlerExecutionPriority { get; }

    /// <summary>
    ///     Invoked after the handler has run — on both the success and the exception paths — with the request's
    ///     <see cref="RequestOutcome" /> (the value it returned, or the exception it threw). Read the outcome to classify
    ///     on what actually happened, e.g. to audit an operation by the verdict it returned.
    /// </summary>
    /// <param name="request">The executable unit that was handled.</param>
    /// <param name="outcome">The outcome of the handler: the value it returned, or the exception it threw.</param>
    /// <param name="serviceProvider">The service provider for resolving dependencies.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task OnAfterHandle(IRequest request, RequestOutcome outcome, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}