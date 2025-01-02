using CQRSharp.Data;
using CQRSharp.Data.Commands;
using CQRSharp.Interfaces.Markers;
using CQRSharp.Interfaces.Markers.Command;
using CQRSharp.Interfaces.Notifications;

namespace CQRSharp.Core.Notifications.Types;

/// <summary>
///     This notification contains information about the completed command and its result.
///     It is used to signal that a command has finished executing and to provide the command's outcome by the dispatcher,
///     but directly before any post-execution attribute methods are executed, before the execution of
///     pre-handling attributes.
/// </summary>
/// <param name="command">The command that was executed.</param>
/// <param name="result">The result of the command execution.</param>
public sealed class CommandCompletedNotification(ICommand command, CommandResult result) : INotification
{
    /// <summary>
    ///     Gets the name of the command that was executed.
    /// </summary>
    /// <remarks>
    ///     This property provides the type name of the command,
    ///     which serves as the identifier of the executed command.
    /// </remarks>
    public string CommandName { get; } = command.GetType().Name;

    /// <summary>
    ///     Gets the result of the command execution.
    /// </summary>
    /// <remarks>
    ///     This property provides the outcome of the command that was executed, indicating whether it was successful or
    ///     failed.
    /// </remarks>
    public CommandResult Result { get; } = result;
}