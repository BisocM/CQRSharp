using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace CQRSharp.Core.Exceptions;

/// <summary>
///     The exception hooks (<c>IRequestExceptionAction</c>s and <c>IRequestExceptionHandler</c>s) of every request, merged
///     from every source-generated module into one invoker per request type. The exception-handling behaviors run it.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IRequestExceptionHookRegistry
{
    /// <summary>
    ///     The exception hook invoker of a request type.
    /// </summary>
    /// <param name="requestType">The runtime type of the request whose exception hooks are being resolved.</param>
    /// <param name="invoker">The invoker, when the request has hooks.</param>
    /// <returns><c>true</c> if the request type has hooks; otherwise <c>false</c>.</returns>
    bool TryGetInvoker(Type requestType, [NotNullWhen(true)] out RequestExceptionHookInvoker? invoker);
}

/// <summary>
///     Runs the exception hooks (actions, then handlers) of one request for an exception it failed with.
/// </summary>
/// <param name="services">The scope to resolve the hooks from.</param>
/// <param name="request">The request that failed.</param>
/// <param name="exception">The exception it failed with.</param>
/// <param name="cancellationToken">The dispatch's cancellation token.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public delegate Task<RequestExceptionHandlingOutcome> RequestExceptionHookInvoker(
    IServiceProvider services,
    object request,
    Exception exception,
    CancellationToken cancellationToken);
