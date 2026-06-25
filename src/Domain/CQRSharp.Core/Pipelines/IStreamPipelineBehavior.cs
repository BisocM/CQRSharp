using CQRSharp.Abstractions.Interfaces.Markers.Request;

namespace CQRSharp.Core.Pipelines;

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
        Func<CancellationToken, IAsyncEnumerable<TItem>> next,
        CancellationToken cancellationToken);
}