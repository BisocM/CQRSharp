namespace CQRSharp.Core.Exceptions;

/// <summary>
///     Provides AOT-safe request exception hook dispatch by request type.
///     Implementations are expected to be provided by the CQRSharp source generator.
/// </summary>
public interface IRequestExceptionHookRegistry
{
    /// <summary>
    ///     Attempts to resolve the exception hook invoker registered for the given request type.
    /// </summary>
    /// <param name="requestType">The runtime type of the request whose exception hooks are being resolved.</param>
    /// <param name="invoker">
    ///     When this method returns <c>true</c>, contains the <see cref="RequestExceptionHookInvoker" /> for
    ///     <paramref name="requestType" />; otherwise the default value.
    /// </param>
    /// <returns><c>true</c> if an invoker is registered for the request type; otherwise <c>false</c>.</returns>
    bool TryGetInvoker(Type requestType, out RequestExceptionHookInvoker invoker);
}

/// <summary>
///     Invokes exception hooks (actions + handlers) for a specific request type.
/// </summary>
public delegate Task<RequestExceptionHandlingOutcome> RequestExceptionHookInvoker(
    IServiceProvider services,
    object request,
    Exception exception,
    CancellationToken cancellationToken);

