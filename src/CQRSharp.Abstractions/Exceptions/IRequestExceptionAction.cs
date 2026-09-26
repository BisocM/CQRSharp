
namespace CQRSharp;

/// <summary>
///     Runs a side effect (a log line, a metric, an alert) when a request of type <typeparamref name="TRequest" /> fails
///     with a <typeparamref name="TException" /> or an exception derived from it. An action never handles the exception:
///     it still reaches the <see cref="IRequestExceptionHandler{TRequest,TResponse,TException}" />s and then the caller.
/// </summary>
/// <remarks>
///     Actions run in the exception-handling behavior (registered by <c>AddCqrsGenerated</c> unless
///     <c>UseExceptionHandling(false)</c> turns it off), before any exception handler, for every exception type they
///     match. They do not run for an <see cref="OperationCanceledException" /> raised after the caller's own token was
///     cancelled. The source generator registers every public or internal, non-generic implementation it finds.
/// </remarks>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TException">The exception type.</typeparam>
public interface IRequestExceptionAction<in TRequest, in TException>
    where TRequest : IRequest
    where TException : Exception
{
    /// <summary>
    ///     Runs the action for the failed request.
    /// </summary>
    /// <param name="request">The request that failed.</param>
    /// <param name="exception">The exception it failed with.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>A task that completes when the action is done.</returns>
    Task Execute(TRequest request, TException exception, CancellationToken cancellationToken);
}