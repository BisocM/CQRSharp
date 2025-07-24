using CQRSharp.Abstractions.Data.Attributes.Notifications;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Data.Interfaces.Notifications;

namespace CQRSharp.Core.Notifications.Types;

/// <summary>
///     Represents a notification that is published when a command is initiated.
///     This notification is published by the RequestDispatcher automatically
///     right before the execution of the command is initiated.
/// </summary>
[NotificationName("cqrsharp.core.command.initiated")]
public sealed class CommandInitiatedNotification : INotification
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="CommandInitiatedNotification" /> class.
    /// </summary>
    /// <param name="command">
    ///     The command for which the notification is raised. This typically implements
    ///     <see cref="ICommand" /> and may also be a <see cref="RequestBase{TContext}" />, which has a
    ///     <see cref="RequestBase{TContext}.Context" />
    /// </param>
    public CommandInitiatedNotification(ICommand command)
    {
        Command = command;
        CommandName = command.GetType().Name;
    }

    /// <summary>
    ///     Gets the command associated with this notification.
    /// </summary>
    /// <remarks>
    ///     The <see cref="Command" /> property provides direct access to the command object being executed.
    ///     You can inspect or cast it if you need more specific details about the request.
    /// </remarks>
    public ICommand Command { get; }

    /// <summary>
    ///     Gets the name of the command associated with the notification.
    /// </summary>
    /// <remarks>
    ///     The <c>CommandName</c> property retrieves the name of the command type that initiated
    ///     the notification. This is accomplished by accessing the <c>Name</c> property of the
    ///     command's <see cref="System.Type" />.
    /// </remarks>
    public string CommandName { get; }
}