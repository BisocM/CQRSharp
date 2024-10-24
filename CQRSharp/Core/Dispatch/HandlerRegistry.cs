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

        public Type? GetCommandTypeByName(string commandName) => handlerMappings.FirstOrDefault(x => x.Value.Name == commandName).Key;
    }
}