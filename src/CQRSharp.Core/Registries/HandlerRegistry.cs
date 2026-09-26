using System.Collections.Frozen;

namespace CQRSharp.Core.Registries;

/// <summary>The merged <see cref="IHandlerRegistry" /> the module composition builds.</summary>
internal sealed class HandlerRegistry(FrozenDictionary<Type, Delegate> invokers) : IHandlerRegistry
{
    public Delegate? TryGetInvoker(Type requestType) => invokers.GetValueOrDefault(requestType);
}
