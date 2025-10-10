using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Data.Models.Commands;

namespace CQRSharp.Abstractions.Data.Interfaces.Markers.Command;

/// <summary>
///     Marker interface for commands.
/// </summary>
public interface ICommand : IRequest<CommandResult>;