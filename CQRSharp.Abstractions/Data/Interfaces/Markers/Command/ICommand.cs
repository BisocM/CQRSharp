using CQRSharp.Abstractions.Data.Attributes.Requests;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;

namespace CQRSharp.Abstractions.Data.Interfaces.Markers.Command;

/// <summary>
///     Marker interface for commands.
/// </summary>
[RequestMarker(RequestKind.Command)]
public interface ICommand : IRequest;