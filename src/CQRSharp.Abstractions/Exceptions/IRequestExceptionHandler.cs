
namespace CQRSharp;

/// <summary>
///     Handles a <typeparamref name="TException" /> (or an exception derived from it) that a request of type
///     <typeparamref name="TRequest" /> fails with, and can turn it into a response the caller receives instead.
/// </summary>
/// <remarks>
///     Handlers run in the exception-handling behavior (registered by <c>AddCqrsGenerated</c> unless
///     <c>UseExceptionHandling(false)</c> turns it off), after every matching
///     <see cref="IRequestExceptionAction{TRequest,TException}" />, from the most derived exception type up, until one
///     calls <see cref="RequestExceptionHandlerState{TResponse}.SetHandled" />. An exception no handler handles reaches the
///     caller unchanged. They do not run for an <see cref="OperationCanceledException" /> raised after the caller's own
///     token was cancelled. For a streaming request the response is the rest of the stream, yielded after the items
///     already produced. The source generator registers every public or internal, non-generic implementation it finds.
/// </remarks>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">
///     Exactly the response the request is dispatched with: <see cref="CommandResult" /> for an <see cref="ICommand" />,
///     <see cref="CommandResult{TResult}" /> for an <see cref="ICommand{TResult}" />, the query's result type, or
///     <see cref="IAsyncEnumerable{T}" /> for a stream. A handler declared over any other response never runs (CQRGEN017).
/// </typeparam>
/// <typeparam name="TException">The exception type.</typeparam>
public interface IRequestExceptionHandler<in TRequest, TResponse, in TException>
    where TRequest : IRequest<TResponse>
    where TException : Exception
{
    /// <summary>
    ///     Handles the exception. Call <see cref="RequestExceptionHandlerState{TResponse}.SetHandled" /> on
    ///     <paramref name="state" /> to mark it handled and supply the response; return without calling it to leave the
    ///     exception to the next handler.
    /// </summary>
    /// <param name="request">The request that failed.</param>
    /// <param name="exception">The exception it failed with.</param>
    /// <param name="state">Where the handler records that it handled the exception, and with which response.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>A task that completes when the handler is done.</returns>
    Task Handle(
        TRequest request,
        TException exception,
        RequestExceptionHandlerState<TResponse> state,
        CancellationToken cancellationToken);
}