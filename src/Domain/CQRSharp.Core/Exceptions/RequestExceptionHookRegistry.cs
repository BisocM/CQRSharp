using System.Collections.Concurrent;

namespace CQRSharp.Core.Exceptions;

public sealed class RequestExceptionHookRegistry : IRequestExceptionHookRegistry
{
    private readonly ConcurrentDictionary<Type, RequestExceptionHookInvoker> _invokers;

    public RequestExceptionHookRegistry()
        : this(new ConcurrentDictionary<Type, RequestExceptionHookInvoker>())
    {
    }

    public RequestExceptionHookRegistry(ConcurrentDictionary<Type, RequestExceptionHookInvoker> invokers)
    {
        _invokers = invokers ?? throw new ArgumentNullException(nameof(invokers));
    }

    public bool TryGetInvoker(Type requestType, out RequestExceptionHookInvoker invoker)
        => _invokers.TryGetValue(requestType, out invoker!);
}

