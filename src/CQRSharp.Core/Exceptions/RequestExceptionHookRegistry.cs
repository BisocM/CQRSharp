using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace CQRSharp.Core.Exceptions;

/// <summary>The merged <see cref="IRequestExceptionHookRegistry" /> the module composition builds.</summary>
internal sealed class RequestExceptionHookRegistry(FrozenDictionary<Type, RequestExceptionHookInvoker> invokers) : IRequestExceptionHookRegistry
{
    /// <summary>A registry with no hooks, until the module composition replaces it.</summary>
    public static RequestExceptionHookRegistry Empty { get; } = new(FrozenDictionary<Type, RequestExceptionHookInvoker>.Empty);

    public bool TryGetInvoker(Type requestType, [NotNullWhen(true)] out RequestExceptionHookInvoker? invoker)
        => invokers.TryGetValue(requestType, out invoker);
}
