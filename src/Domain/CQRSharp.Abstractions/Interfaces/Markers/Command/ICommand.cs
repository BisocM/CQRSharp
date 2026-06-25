using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Models.Commands;

namespace CQRSharp.Abstractions.Interfaces.Markers.Command;

/// <summary>
///     Marker interface for commands.
/// </summary>
public interface ICommand : IRequest<CommandResult>;