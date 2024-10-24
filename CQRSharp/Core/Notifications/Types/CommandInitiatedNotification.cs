using CQRSharp.Interfaces.Markers;
using CQRSharp.Interfaces.Markers.Command;

namespace CQRSharp.Core.Notifications.Types
{
    public sealed class CommandInitiatedNotification(ICommand command) : INotification
    {
        public string CommandName { get; } = command.GetType().Name;
    }
}