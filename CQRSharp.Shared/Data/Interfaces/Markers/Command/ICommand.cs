using CQRSharp.Shared.Data.Attributes.Requests;
using CQRSharp.Shared.Data.Interfaces.Markers.Request;

namespace CQRSharp.Shared.Data.Interfaces.Markers.Command;

/// <summary>
///     Marker interface for commands.
/// </summary>
[RequestMarker(RequestKind.Command)]
public interface ICommand : IRequest;