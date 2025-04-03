using CQRSharp.Core.Caching.Requests;
using CQRSharp.Interfaces.Context;

namespace CQRSharp.Interfaces.Markers.Request;

/// <summary>
///     Marker interface to indicate that a class is used to handle a request. Major inheritors are ICommand and IQuery.
/// </summary>
public interface IRequest
{
    public IRequestContext? Context { get; set; }
    public RequestMetadata? Metadata { get; set; }
}