using CQRSharp;

namespace CQRSharp;

/// <summary>
///     Marker interface for commands. A command reports only a <see cref="CommandResult" /> outcome; for a command that
///     must also return a value, see <see cref="ICommand{TResult}" />.
/// </summary>
public interface ICommand : IRequest<CommandResult>, ICommandMarker;