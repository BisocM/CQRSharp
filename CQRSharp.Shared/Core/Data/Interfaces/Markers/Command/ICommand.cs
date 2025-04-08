using CQRSharp.Shared.Attributes.Requests;
using CQRSharp.Shared.Core.Data.Interfaces.Markers.Request;

namespace CQRSharp.Shared.Core.Data.Interfaces.Markers.Command;

/// <summary>
///     Marker interface for commands.
/// </summary>
[RequestMarker(RequestKind.Command)]
public interface ICommand : IRequest;