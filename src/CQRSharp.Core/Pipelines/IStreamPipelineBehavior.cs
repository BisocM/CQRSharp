using CQRSharp.Abstractions.Interfaces.Markers.Request;

namespace CQRSharp.Core.Pipelines;

/// <summary>
///     The continuation a stream pipeline behavior invokes to run the next behavior (or the stream handler). The
///     cancellation token is defaulted so a behavior can simply <c>next()</c> to flow the ambient token. Named (rather
///     than a bare <see cref="Func{T, TResult}" />) so it self-documents on hover and the parameterless call compiles.
/// </summary>
/// <typeparam name="TItem">The streamed element type.</typeparam>
public delegate IAsyncEnumerable<TItem> StreamHandlerDelegate<TItem>(CancellationToken cancellationToken = default);

/// <summary>
///     Defines an interface for pipeline behaviors that wrap streaming requests.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TItem">The streamed element type.</typeparam>
public interface IStreamPipelineBehavior<in TRequest, TItem> where TRequest : IRequest
{
    /// <summary>
    ///     Handles the request by invoking the next behavior in the stream pipeline or the stream handler.
    /// </summary>
    IAsyncEnumerable<TItem> Handle(
        TRequest request,
        StreamHandlerDelegate<TItem> next,
        CancellationToken cancellationToken);
}