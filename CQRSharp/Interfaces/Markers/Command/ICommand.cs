using CQRSharp.Interfaces.Markers.Request;
using CQRSharp.Shared.Attributes;
using CQRSharp.Shared.Attributes.Requests;

namespace CQRSharp.Interfaces.Markers.Command;

/// <summary>
///     Marker interface for commands.
/// </summary>
[RequestMarker(RequestKind.Command)]
public interface ICommand : IRequest;