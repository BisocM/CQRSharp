using CQRSharp.Data;
using CQRSharp.Interfaces.Markers.Command;

namespace CQRSharp.Interfaces.Handlers
{
    /// <summary>
    /// Interface for handling commands that do not return a result.
    /// </summary>
    /// <typeparam name="TCommand">The type of the command.</typeparam>
    public interface ICommandHandler<in TCommand> where TCommand : ICommand
    {
        Task<CommandResult> Handle(TCommand command, CancellationToken cancellationToken);
    }
}