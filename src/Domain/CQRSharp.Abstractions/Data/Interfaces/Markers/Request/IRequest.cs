using CQRSharp.Abstractions.Data.Interfaces.Context;
using CQRSharp.Abstractions.Data.Models.Requests;

namespace CQRSharp.Abstractions.Data.Interfaces.Markers.Request;

/// <summary>
///     Marker interface to indicate that a class is used to handle a request. Major inheritors are ICommand and IQuery.
/// </summary>
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