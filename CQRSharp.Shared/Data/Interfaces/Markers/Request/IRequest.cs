using CQRSharp.Shared.Data.Attributes.Requests;
using CQRSharp.Shared.Data.Interfaces.Context;
using CQRSharp.Shared.Data.Models.Requests;

namespace CQRSharp.Shared.Data.Interfaces.Markers.Request;

/// <summary>
///     Marker interface to indicate that a class is used to handle a request. Major inheritors are ICommand and IQuery.
/// </summary>
[RequestMarker(RequestKind.Unknown)]
public interface IRequest
{
    public IRequestContext? Context { get; set; }
    public RequestMetadata? Metadata { get; set; }
}