using CQRSharp.Abstractions.Data.Attributes.Notifications;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Data.Interfaces.Notifications;
using CQRSharp.Abstractions.Data.Models.Commands;

namespace CQRSharp.Core.Notifications.Types;

/// <summary>
///     This notification contains information about the completed command and its result.
///     It is used to signal that a command has finished executing and to provide the command's outcome by the dispatcher,
///     but directly before any post-execution attribute methods are executed.
/// </summary>
[NotificationName("cqrsharp.core.command.completed")]
public sealed class CommandCompletedNotification : INotification
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="CommandCompletedNotification" /> class.
    /// </summary>
    /// <param name="command">
    ///     The command that was executed. Typically implements <see cref="ICommand" />, and may also
    ///     be a <see cref="RequestBase{TContext}" /> containing a context.
    /// </param>
    /// <param name="result">
    ///     The outcome of the command execution, represented by a <see cref="CommandResult" /> indicating success or failure.
    /// </param>
    public CommandCompletedNotification(ICommand command, CommandResult result)
    {
        Command = command;
        CommandName = command.GetType().Name;
        Result = result;
    }

    /// <summary>
    ///     Gets the command that was executed.
    /// </summary>
    /// <remarks>
    ///     The <see cref="Command" /> property provides direct access to the command object
    ///     which can be useful for logging or additional introspection.
    /// </remarks>
    public ICommand Command { get; }

    /// <summary>
    ///     Gets the name of the command that was executed.
    /// </summary>
    /// <remarks>
    ///     This property provides the type name of the command,
    ///     which serves as the identifier of the executed command.
    /// </remarks>
    public string CommandName { get; }

    /// <summary>
    ///     Gets the result of the command execution.
    /// </summary>
    /// <remarks>
    ///     This property provides the outcome of the command that was executed, indicating whether it was successful or
    ///     failed.
    /// </remarks>
    public CommandResult Result { get; }
}