using CQRSharp.Data;
using CQRSharp.Interfaces.Markers;

namespace CQRSharp.Interfaces.Handlers
{
    /// <summary>
    /// Interface for handling commands that do not return a result.
    /// </summary>
    /// <typeparam name="TCommand">The type of the command.</typeparam>
    public interface ICommandHandler<TCommand> where TCommand : ICommand
    {
        Task<Unit> Handle(TCommand command, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Interface for handling commands that return a result of type <typeparamref name="TResult"/>.
    /// </summary>
    /// <typeparam name="TCommand">The type of the command.</typeparam>
    /// <typeparam name="TResult">The type of the result returned by the command.</typeparam>
    public interface ICommandHandler<TCommand, TResult> where TCommand : ICommand<TResult>
    {
        Task<TResult> Handle(TCommand command, CancellationToken cancellationToken);
    }
}