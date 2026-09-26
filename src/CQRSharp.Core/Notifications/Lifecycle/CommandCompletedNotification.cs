namespace CQRSharp;

/// <summary>
///     Published when a command completed: its handler returned and its post-handlers ran without throwing. Every
///     command publishes it, one that returns a value (<see cref="ICommand{TResult}" />) included.
/// </summary>
/// <remarks>
///     A command whose handler <em>returns</em> a failed <see cref="CommandResult" /> still completes: the result says it
///     failed. <see cref="CommandFailedNotification" /> is published instead when something throws.
/// </remarks>
public sealed class CommandCompletedNotification : INotification
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="CommandCompletedNotification" /> class.
    /// </summary>
    /// <param name="command">The command that completed.</param>
    /// <param name="result">
    ///     The command's result. For an <see cref="ICommand{TResult}" /> it is the <see cref="CommandResult{TResult}" />
    ///     the handler returned, typed here as its <see cref="CommandResult" /> outcome.
    /// </param>
    public CommandCompletedNotification(ICommandMarker command, CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(result);
        Command = command;
        CommandName = command.GetType().Name;
        Result = result;
    }

    /// <summary>Gets the command that completed: an <see cref="ICommand" /> or an <see cref="ICommand{TResult}" />.</summary>
    public ICommandMarker Command { get; }

    /// <summary>Gets the name of the command's type.</summary>
    public string CommandName { get; }

    /// <summary>Gets the command's outcome, successful or failed.</summary>
    public CommandResult Result { get; }
}
