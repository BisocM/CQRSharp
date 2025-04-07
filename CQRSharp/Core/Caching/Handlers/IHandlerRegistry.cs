namespace CQRSharp.Core.Caching.Handlers;

/// <summary>
/// Represents a registry for storing and retrieving handler delegates associated with specific request types.
/// </summary>
public interface IHandlerRegistry
{
    /// <summary>
    /// Attempts to retrieve the handler delegate for the specified request type.
    /// </summary>
    /// <param name="requestType">The type of the request to find a corresponding handler delegate for.</param>
    /// <param name="invokerDelegate">
    /// When this method returns, contains the handler delegate associated with the specified request type,
    /// if the handler is found; otherwise, null.
    /// This parameter is passed uninitialized.
    /// </param>
    /// <returns>
    /// True if a handler delegate for the specified request type exists; otherwise, false.
    /// </returns>
    public bool TryGetHandlerDelegate(Type requestType, out HandlerInvokerDelegate? invokerDelegate);
}

/// <summary>
/// Represents a delegate that handles the invocation of a specified handler
/// with the provided request and cancellation token.
/// </summary>
/// <param name="handler">The target handler to invoke.</param>
/// <param name="request">The request object to be processed by the handler.</param>
/// <param name="cancellationToken">A token used to propagate notification that operations should be canceled.</param>
/// <returns>A task representing the asynchronous invocation, returning an object as the result.</returns>
public delegate Task<object> HandlerInvokerDelegate(object handler, object request, CancellationToken cancellationToken);