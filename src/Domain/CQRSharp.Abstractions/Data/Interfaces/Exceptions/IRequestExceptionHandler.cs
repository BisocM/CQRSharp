using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Data.Models.Exceptions;

namespace CQRSharp.Abstractions.Data.Interfaces.Exceptions;

/// <summary>
///     Handles exceptions thrown during request processing and can optionally convert them into a response.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
/// <typeparam name="TException">The exception type.</typeparam>
public interface IRequestExceptionHandler<in TRequest, TResponse, in TException>
    where TRequest : IRequest<TResponse>
    where TException : Exception
{
    /// <summary>
    ///     Executes exception handling logic. Call <see cref="RequestExceptionHandlerState{TResponse}.SetHandled" />
    ///     to mark the exception as handled and provide a response.
    /// </summary>
    Task Handle(
        TRequest request,
        TException exception,
        RequestExceptionHandlerState<TResponse> state,
        CancellationToken cancellationToken);
}

