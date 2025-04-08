using CQRSharp.Shared.Attributes.Requests;
using CQRSharp.Shared.Core.Data.Interfaces.Context;
using CQRSharp.Shared.Core.Data.Models.Requests;

namespace CQRSharp.Shared.Core.Data.Interfaces.Markers.Request;

/// <summary>
///     Marker interface to indicate that a class is used to handle a request. Major inheritors are ICommand and IQuery.
/// </summary>
[RequestMarker(RequestKind.Unknown)]
public interface IRequest
{
    public IRequestContext? Context { get; set; }
    public RequestMetadata? Metadata { get; set; }
}