namespace CQRSharp.Pipelines;

/// <summary>
///     The continuation a stream pipeline behavior invokes to run the next behavior (or the stream handler). The
///     cancellation token is defaulted so a behavior can simply <c>next()</c> to flow the ambient token. Named (rather
///     than a bare <see cref="Func{T, TResult}" />) so it self-documents on hover and the parameterless call compiles.
/// </summary>
/// <typeparam name="TItem">The streamed element type.</typeparam>
public delegate IAsyncEnumerable<TItem> StreamHandlerDelegate<TItem>(CancellationToken cancellationToken = default);

/// <summary>
///     A behavior that wraps a streaming request run with <c>Stream</c>: the stream counterpart of
///     <see cref="IPipelineBehavior{TRequest,TResult}" />. It runs when the stream is enumerated and can observe, filter or
///     replace the items the rest of the pipeline yields.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TItem">The streamed element type.</typeparam>
public interface IStreamPipelineBehavior<in TRequest, TItem> where TRequest : IRequest
{
    /// <summary>
    ///     Handles the request by invoking the next behavior in the stream pipeline or the stream handler.
    /// </summary>
    /// <param name="request">The streaming request being handled.</param>
    /// <param name="next">The continuation; enumerate <c>next()</c> to run the rest of the pipeline.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The stream the consumer enumerates.</returns>
    IAsyncEnumerable<TItem> Handle(
        TRequest request,
        StreamHandlerDelegate<TItem> next,
        CancellationToken cancellationToken);
}