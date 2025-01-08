using CQRSharp.Interfaces.Markers.Command;
using CQRSharp.Interfaces.Notifications;

namespace CQRSharp.Core.Notifications.Types;

/// <summary>
///     Represents a notification that is published when a command is initiated.
///     This notification is published by the Dispatcher automatically
///     right before the execution of the command is initiated.
/// </summary>
public sealed class CommandInitiatedNotification(ICommand command) : INotification
{
    /// <summary>
    ///     Gets the name of the command associated with the notification.
    /// </summary>
    /// <remarks>
    ///     The <c>CommandName</c> property retrieves the name of the command type that initiated
    ///     the notification. This is accomplished by accessing the <c>Name</c> property of the
    ///     command's <see cref="System.Type" />.
    /// </remarks>
    public string CommandName { get; } = command.GetType().Name;
}