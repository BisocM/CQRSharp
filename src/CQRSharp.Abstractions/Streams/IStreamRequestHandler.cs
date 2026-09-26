namespace CQRSharp;

/// <summary>
///     Handles a streaming request that yields elements of type <typeparamref name="TItem" />. The request's typed
///     context, if it declares one, is on <c>request.Context</c> (see <see cref="RequestBase{TContext}" />).
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TItem">The streamed element type.</typeparam>
public interface IStreamRequestHandler<in TRequest, TItem> where TRequest : IStreamRequest<TItem>
{
    /// <summary>
    ///     Handles the request and returns an <see cref="IAsyncEnumerable{T}" /> representing the stream.
    /// </summary>
    /// <param name="request">The request to handle.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The stream of elements.</returns>
    IAsyncEnumerable<TItem> Handle(TRequest request, CancellationToken cancellationToken);
}
