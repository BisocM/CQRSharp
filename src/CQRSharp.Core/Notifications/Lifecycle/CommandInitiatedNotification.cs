namespace CQRSharp;

/// <summary>
///     Published when the executor starts a command, before its pre-handlers run. Every command publishes it, one that
///     returns a value (<see cref="ICommand{TResult}" />) included.
/// </summary>
/// <remarks>
///     Exactly one terminal notification follows it: <see cref="CommandCompletedNotification" /> when the command
///     succeeds, or <see cref="CommandFailedNotification" /> when it fails.
/// </remarks>
public sealed class CommandInitiatedNotification : INotification
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="CommandInitiatedNotification" /> class.
    /// </summary>
    /// <param name="command">The command being started.</param>
    public CommandInitiatedNotification(ICommandMarker command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Command = command;
        CommandName = command.GetType().Name;
    }

    /// <summary>Gets the command being started: an <see cref="ICommand" /> or an <see cref="ICommand{TResult}" />.</summary>
    public ICommandMarker Command { get; }

    /// <summary>Gets the name of the command's type.</summary>
    public string CommandName { get; }
}
