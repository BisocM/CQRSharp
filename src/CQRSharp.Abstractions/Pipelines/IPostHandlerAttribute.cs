
namespace CQRSharp;

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
    /// <remarks>
    ///     Every post-handler runs, like nested <c>finally</c> blocks: one that throws never stops the others. On a request
    ///     that had succeeded, the first post-handler to throw fails the request, and the post-handlers after it observe
    ///     that exception as the outcome; any other post-handler exception is logged and never replaces the request's own.
    /// </remarks>
    /// <param name="request">The executable unit that was handled.</param>
    /// <param name="outcome">
    ///     The outcome of the request so far: the value the handler returned, or the exception the request failed with.
    /// </param>
    /// <param name="serviceProvider">The service provider for resolving dependencies.</param>
    /// <param name="cancellationToken">
    ///     The request's token while the outcome is a success. When the outcome is a failure it is
    ///     <see cref="CancellationToken.None" />: the request's own token may be the one that was cancelled, and the
    ///     post-handler must still be able to record the failure.
    /// </param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task OnAfterHandle(IRequest request, RequestOutcome outcome, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}