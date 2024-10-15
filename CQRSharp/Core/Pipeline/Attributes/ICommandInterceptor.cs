namespace CQRSharp.Core.Pipeline.Attributes
{
    /// <summary>
    /// Marker interface for command interceptors.
    /// This means that the behavior handled in the implemented method will be fired both before and after the command is executed.
    /// </summary>
    public interface ICommandInterceptor : IPreHandlerAttribute, IPostHandlerAttribute { }
}