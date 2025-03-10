using CQRSharp.Core.Caching.Requests;
using CQRSharp.Interfaces.Context;

namespace CQRSharp.Interfaces.Markers.Request;

/// <summary>
///     Marker interface to indicate that a class is used to handle a request. Major inheritors are ICommand and IQuery.
/// </summary>
public interface IRequest
{
    /// <summary>
    ///     A context object that stores request-level metadata like RequestId and UserId.
    /// </summary>
    public IRequestContext? Context { get; set; }

    /// <summary>
    /// Metadata related to the request. Contains runtime-specific data.
    /// </summary>
    public RequestMetadata? Metadata { get; set; }
}