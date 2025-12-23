using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;

namespace CQRSharp.Abstractions.Data.Interfaces.Exceptions;

/// <summary>
///     Executes side effects when an exception occurs during request processing.
///     Actions do not mark an exception as handled and never suppress it on their own.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TException">The exception type.</typeparam>
public interface IRequestExceptionAction<in TRequest, in TException>
    where TRequest : IRequest
    where TException : Exception
{
    /// <summary>
    ///     Executes the action for the given request/exception pair.
    /// </summary>
    Task Execute(TRequest request, TException exception, CancellationToken cancellationToken);
}

