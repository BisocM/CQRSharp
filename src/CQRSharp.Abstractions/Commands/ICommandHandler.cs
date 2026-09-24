namespace CQRSharp;

/// <summary>
///     Handles a command that reports only an outcome. The command's typed context, if it declares one, is on
///     <c>command.Context</c> (see <see cref="RequestBase{TContext}" />).
/// </summary>
/// <typeparam name="TCommand">The type of the command.</typeparam>
public interface ICommandHandler<in TCommand> where TCommand : ICommand
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
