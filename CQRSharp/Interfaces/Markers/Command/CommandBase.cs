using CQRSharp.Interfaces.Markers.Request;

namespace CQRSharp.Interfaces.Markers.Command
{
    /// <summary>
    /// Base class for commands.
    /// </summary>
    public abstract class CommandBase : RequestBase, ICommand { }
}