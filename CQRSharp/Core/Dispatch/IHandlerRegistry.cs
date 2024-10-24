namespace CQRSharp.Core.Dispatch
{
    public interface IHandlerRegistry
    {
        Type? GetHandlerType(Type requestType);
        Type? GetCommandTypeByName(string commandName);
    }
}