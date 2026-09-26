namespace CQRSharp;

/// <summary>
///     Handles a value-returning command (<see cref="ICommand{TResult}" />), producing a
///     <see cref="CommandResult{TResult}" /> outcome that carries the minted value on success. The command's typed
///     context, if it declares one, is on <c>command.Context</c> (see <see cref="RequestBase{TContext}" />).
/// </summary>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TResult">The value the command returns on success.</typeparam>
public interface IResultCommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult>
{
    /// <summary>
    ///     Handles the command and returns its <see cref="CommandResult{TResult}" /> outcome (the value on success, or
    ///     an error via <see cref="CommandResult{TResult}.FromError(string, int?)" />).
    /// </summary>
    /// <param name="command">The command to handle.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task whose result is the command's outcome.</returns>
    Task<CommandResult<TResult>> Handle(TCommand command, CancellationToken cancellationToken);
}
