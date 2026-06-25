using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Interfaces.Notifications;

namespace CQRSharp.Core.Notifications.Types;

/// <summary>
///     Published when a command's handler throws, signalling that the command did not complete successfully.
/// </summary>
/// <remarks>
///     Complements <see cref="CommandCompletedNotification" />: after a <see cref="CommandInitiatedNotification" />,
///     exactly one of <see cref="CommandCompletedNotification" /> (success) or <see cref="CommandFailedNotification" />
///     (handler threw) is published for a given execution.
/// </remarks>
public sealed class CommandFailedNotification : INotification
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="CommandFailedNotification" /> class.
    /// </summary>
    /// <param name="command">The command whose handler threw.</param>
    /// <param name="exception">The exception thrown by the handler.</param>
    public CommandFailedNotification(ICommand command, Exception exception)
    {
        Command = command;
        CommandName = command.GetType().Name;
        Exception = exception;
    }

    /// <summary>Gets the command whose handler threw.</summary>
    public ICommand Command { get; }

    /// <summary>Gets the type name of the failed command.</summary>
    public string CommandName { get; }

    /// <summary>Gets the exception thrown by the handler.</summary>
    public Exception Exception { get; }
}
