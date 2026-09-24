namespace CQRSharp;

/// <summary>
///     Published when a started command fails: a pre-handler, the handler or a post-handler threw, the command was
///     cancelled, or it ran out of time. Every command publishes it, one that returns a value
///     (<see cref="ICommand{TResult}" />) included.
/// </summary>
/// <remarks>
///     After a <see cref="CommandInitiatedNotification" />, exactly one of <see cref="CommandCompletedNotification" /> or
///     <see cref="CommandFailedNotification" /> is published. It is delivered under no cancellation token, since the
///     command's own may be the one that was cancelled; a subscriber that fails is logged and never replaces the
///     command's exception.
/// </remarks>
public sealed class CommandFailedNotification : INotification
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="CommandFailedNotification" /> class.
    /// </summary>
    /// <param name="command">The command that failed.</param>
    /// <param name="exception">The exception the command failed with, which its caller receives.</param>
    public CommandFailedNotification(ICommandMarker command, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(exception);
        Command = command;
        CommandName = command.GetType().Name;
        Exception = exception;
    }

    /// <summary>Gets the command that failed: an <see cref="ICommand" /> or an <see cref="ICommand{TResult}" />.</summary>
    public ICommandMarker Command { get; }

    /// <summary>Gets the name of the command's type.</summary>
    public string CommandName { get; }

    /// <summary>Gets the exception the command failed with.</summary>
    public Exception Exception { get; }
}
