using System.Collections.Generic;

namespace CQRSharp.Core.Caching.Handlers;

/// <inheritdoc />
public class HandlerRegistry(IReadOnlyDictionary<Type, HandlerInvokerDelegate> handlerMap) : IHandlerRegistry
{
    /// <inheritdoc />
    public bool TryGetHandlerDelegate(Type requestType, out HandlerInvokerDelegate? invokerDelegate)
    {
        return handlerMap.TryGetValue(requestType, out invokerDelegate);
    }
}