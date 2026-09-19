namespace CQRSharp.Core.Caching.Handlers;

/// <summary>
///     Represents a registry for storing and retrieving handler delegates associated with specific request types.
/// </summary>
public interface IHandlerRegistry
{
    /// <summary>
    ///     Attempts to retrieve the handler delegate for the specified request type.
    /// </summary>
    /// <param name="requestType">The type of the request to find a corresponding handler delegate for.</param>
    /// <param name="invokerDelegate">
    ///     When this method returns, contains the handler delegate associated with the specified request type,
    ///     if the handler is found; otherwise, null.
    ///     This parameter is passed uninitialized.
    /// </param>
    /// <returns>
    ///     True if a handler delegate for the specified request type exists; otherwise, false.
    /// </returns>
    public bool TryGetHandlerDelegate(Type requestType, out HandlerInvokerDelegate? invokerDelegate);

    /// <summary>
    ///     The source-generated typed invoker for a command/query: a
    ///     <c>Func&lt;object, TRequest, CancellationToken, Task&lt;TResult&gt;&gt;</c> that returns the handler's own task,
    ///     so the hot path neither boxes the result nor adds a state machine. <c>null</c> when none was generated.
    /// </summary>
    public Delegate? TryGetTypedInvoker(Type requestType) => null;
}

/// <summary>
///     Represents a delegate that handles the invocation of a specified handler
///     with the provided request and cancellation token.
/// </summary>
/// <param name="handler">The target handler to invoke.</param>
/// <param name="request">The request object to be processed by the handler.</param>
/// <param name="cancellationToken">A token used to propagate notification that operations should be canceled.</param>
/// <returns>A task representing the asynchronous invocation, returning the boxed result (nullable for queries).</returns>
public delegate Task<object?>
    HandlerInvokerDelegate(object handler, object request, CancellationToken cancellationToken);