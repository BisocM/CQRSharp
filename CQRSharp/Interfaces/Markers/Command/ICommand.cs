using CQRSharp.Interfaces.Markers.Request;

namespace CQRSharp.Interfaces.Markers.Command
{
    /// <summary>
    /// Marker interface for commands that do not return a result.
    /// </summary>
    public interface ICommand : IRequest { }
}