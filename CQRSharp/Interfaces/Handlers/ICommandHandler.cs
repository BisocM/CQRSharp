using CQRSharp.Data;
using CQRSharp.Interfaces.Markers;

namespace CQRSharp.Interfaces.Handlers
{
    /// <summary>
    /// Interface for handling commands that do not return a result.
    /// </summary>
    /// <typeparam name="TCommand">The type of the command.</typeparam>
    public interface ICommandHandler<in TCommand> where TCommand : ICommand
    {
        Task<Unit> Handle(TCommand command, CancellationToken cancellationToken);
    }
}