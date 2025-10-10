using CQRSharp.Abstractions.Data.Interfaces.Context;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Data.Models.Commands;

namespace CQRSharp.Abstractions.Data.Interfaces.Handlers;

/// <summary>
///     Interface for handling commands that do not return a result.
/// </summary>
/// <typeparam name="TCommand">The type of the command.</typeparam>
/// <typeparam name="TContext">The type of the context object carried by the command.</typeparam>
public interface ICommandHandler<in TCommand, TContext>
    where TCommand : ICommand where TContext : IRequestContext
{
    /// <summary>
    ///     Handles the execution of a command and returns a <see cref="CommandResult" />.
    /// </summary>
    /// <param name="command">The command to be handled.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    ///     A task that represents the asynchronous command handling operation. The task result contains the
    ///     <see cref="CommandResult" /> of the operation.
    /// </returns>
    Task<CommandResult> Handle(TCommand command, CancellationToken cancellationToken);
}

/// <summary>
///     Interface for handling commands that do not return a result.
/// </summary>
/// <typeparam name="TCommand">The type of the command.</typeparam>
public interface ICommandHandler<in TCommand> : ICommandHandler<TCommand, RequestContextBase> where TCommand : ICommand;