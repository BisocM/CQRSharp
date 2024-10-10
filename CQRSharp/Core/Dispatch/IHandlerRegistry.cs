namespace CQRSharp.Core.Dispatch
{
    public interface IHandlerRegistry
    {
        Type? GetHandlerType(Type requestType);
    }
}