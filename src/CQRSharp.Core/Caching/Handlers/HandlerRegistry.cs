using System.Collections.Concurrent;

namespace CQRSharp.Core.Caching.Handlers;

/// <inheritdoc />
public class HandlerRegistry(
    ConcurrentDictionary<Type, HandlerInvokerDelegate> handlerMap,
    ConcurrentDictionary<Type, Delegate>? typedInvokerMap = null) : IHandlerRegistry
{
    /// <inheritdoc />
    public Delegate? TryGetTypedInvoker(Type requestType)
        => typedInvokerMap is not null && typedInvokerMap.TryGetValue(requestType, out var invoker) ? invoker : null;

    /// <inheritdoc />
    public bool TryGetHandlerDelegate(Type requestType, out HandlerInvokerDelegate? invokerDelegate)
    {
        return handlerMap.TryGetValue(requestType, out invokerDelegate);
    }
}