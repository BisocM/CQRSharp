using System.Collections.Concurrent;

namespace CQRSharp.Core.Dispatch
{
    public sealed class HandlerRegistry(ConcurrentDictionary<Type, Type> handlerMappings) : IHandlerRegistry
    {
        public Type? GetHandlerType(Type requestType)
        {
            handlerMappings.TryGetValue(requestType, out var handlerType);
            return handlerType;
        }
    }
}