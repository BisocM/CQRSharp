using CQRSharp.Abstractions.Interfaces.Context;
using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Models.Commands;

namespace CQRSharp.Abstractions.Interfaces.Handlers;

/// <summary>
///     Handles a value-returning command (<see cref="ICommand{TResult}" />), producing a
///     <see cref="CommandResult{TResult}" /> outcome that carries the minted value on success.
/// </summary>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TResult">The value the command returns on success.</typeparam>
/// <typeparam name="TContext">The type of the context object carried by the command.</typeparam>
public interface IResultCommandHandler<in TCommand, TResult, TContext>
    where TCommand : ICommand<TResult>
    where TContext : IRequestContext
{
    /// <summary>
    ///     Handles the command and returns its <see cref="CommandResult{TResult}" /> outcome (the value on success, or
    ///     an error via <see cref="CommandResult{TResult}.FromError" />).
    /// </summary>
    /// <param name="command">The command to handle.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    Task<CommandResult<TResult>> Handle(TCommand command, CancellationToken cancellationToken);
}

/// <summary>
///     Handles a value-returning command using the default <see cref="RequestContextBase" /> context.
/// </summary>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TResult">The value the command returns on success.</typeparam>
public interface IResultCommandHandler<in TCommand, TResult>
    : IResultCommandHandler<TCommand, TResult, RequestContextBase>
    where TCommand : ICommand<TResult>;
