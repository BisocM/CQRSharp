using CQRSharp.Abstractions.Interfaces.Context;
using CQRSharp.Abstractions.Models.Requests;

namespace CQRSharp.Abstractions.Interfaces.Markers.Request;

/// <summary>
///     Marker interface to indicate that a class is used to handle a request. Major inheritors are ICommand and IQuery.
/// </summary>
/// <remarks>
///     A request instance is single-use. While processing a request the dispatcher writes <see cref="Metadata" /> and
///     <see cref="Context" /> onto the instance, so the same instance must not be dispatched more than once or shared
///     across concurrent dispatches — construct a new request object per dispatch.
/// </remarks>
public interface IRequest
{
    /// <summary>
    ///     Gets or sets the context associated with the request.
    ///     The context typically provides additional information required to process the request.
    /// </summary>
    public IRequestContext? Context { get; set; }

    /// <summary>
    ///     Gets or sets the metadata associated with the request.
    ///     The metadata encapsulates information such as request type, handler type,
    ///     and pipeline behaviors, providing critical structural details for processing the request.
    /// </summary>
    public RequestMetadata? Metadata { get; set; }
}

public interface IRequest<out TResponse> : IRequest
{
}