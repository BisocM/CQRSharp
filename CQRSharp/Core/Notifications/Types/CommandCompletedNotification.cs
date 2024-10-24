using CQRSharp.Data;
using CQRSharp.Interfaces.Markers;
using CQRSharp.Interfaces.Markers.Command;

namespace CQRSharp.Core.Notifications.Types
{
    public sealed class CommandCompletedNotification(ICommand command, CommandResult result) : INotification
    {
        public string CommandName { get; } = command.GetType().Name;
        public CommandResult Result { get; } = result;
    }
}