using CQRSharp.Shared.Data.Attributes.Requests;
using CQRSharp.Shared.Data.Interfaces.Context;
using CQRSharp.Shared.Data.Interfaces.Markers.Command;
using CQRSharp.Shared.Data.Models.Commands;

namespace CQRSharp.Shared.Data.Interfaces.Handlers;

/// <summary>
///     Interface for handling commands that do not return a result.
/// </summary>
/// <typeparam name="TCommand">The type of the command.</typeparam>
/// <typeparam name="TContext">The type of the context object carried by the command.</typeparam>
[HandlerType(HandlerKind.Command)]
public interface ICommandHandler<in TCommand, TContext>
    where TCommand : ICommand where TContext : IRequestContext
{
    /// <summary>
    ///     Handles the provided command asynchronously and produces a result indicating the outcome.
    /// </summary>
    /// <typeparam name="TCommand">The type of the command to handle.</typeparam>
    /// <param name="command">The command instance to be processed.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    ///     A task that represents the asynchronous operation.
    ///     The task result contains a <see cref="CommandResult" /> which represents the outcome of the command execution.
    /// </returns>
    Task<CommandResult> Handle(TCommand command, CancellationToken cancellationToken);
}

/// <summary>
///     Interface for handling commands that do not return a result.
/// </summary>
/// <typeparam name="TCommand">The type of the command.</typeparam>
[HandlerType(HandlerKind.Command)]
public interface ICommandHandler<in TCommand> : ICommandHandler<TCommand, RequestContextBase> where TCommand : ICommand;